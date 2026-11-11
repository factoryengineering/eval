using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiConformance;

public sealed class ApiHost : IAsyncDisposable
{
    private readonly IHost host;
    private readonly SqliteConnection keeper;
    private bool disposed;

    private ApiHost(IHost host, SqliteConnection keeper, HttpClient client, string connectionString, IReadOnlyList<Type> dbContextTypes)
    {
        this.host = host;
        this.keeper = keeper;
        Client = client;
        ConnectionString = connectionString;
        DbContextTypes = dbContextTypes;
    }

    public HttpClient Client { get; }
    public string ConnectionString { get; }
    public IReadOnlyList<Type> DbContextTypes { get; }
    public IServiceProvider Services => host.Services;
    public bool IsDisposed => disposed;
    public System.Data.ConnectionState DatabaseState => keeper.State;

    public static ApiHost Start(Assembly generated)
    {
        var setup = generated.GetType("AppSetup")
            ?? generated.GetTypes().FirstOrDefault(t => t.Name == "AppSetup" && t.IsAbstract && t.IsSealed)
            ?? throw new InvalidOperationException("The generated assembly has no static class AppSetup.");
        var configure = setup.GetMethod("ConfigureServices", BindingFlags.Public | BindingFlags.Static, [typeof(IServiceCollection), typeof(string)])
            ?? throw new InvalidOperationException("AppSetup has no public static ConfigureServices(IServiceCollection, string).");

        var connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keeper = new SqliteConnection(connectionString);
        keeper.Open();
        IHost? host = null;
        try
        {
            host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddControllers().AddApplicationPart(generated);
                        configure.Invoke(null, [services, connectionString]);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    }))
                .Build();
            var contextTypes = FindDbContextTypes(generated, host.Services.GetRequiredService<IServiceProviderIsService>());
            using (var scope = host.Services.CreateScope())
                foreach (var type in contextTypes)
                    ((DbContext)scope.ServiceProvider.GetRequiredService(type)).Database.EnsureCreated();
            host.Start();
            return new ApiHost(host, keeper, host.GetTestClient(), connectionString, contextTypes);
        }
        catch
        {
            host?.Dispose();
            keeper.Dispose();
            throw;
        }
    }

    private static List<Type> FindDbContextTypes(Assembly generated, IServiceProviderIsService isService) =>
        generated.GetTypes()
            .Where(t => typeof(DbContext).IsAssignableFrom(t) && !t.IsAbstract && isService.IsService(t))
            .ToList();

    public async Task BumpServerOwned(ServerOwnedField field)
    {
        using var scope = host.Services.CreateScope();
        foreach (var type in DbContextTypes)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(type);
            var entity = context.Model.GetEntityTypes().FirstOrDefault(e => e.ClrType.Name == field.EntityTypeName || e.Name == field.EntityTypeName);
            if (entity is null) continue;
            var table = entity.GetTableName() ?? throw new InvalidOperationException($"Entity '{field.EntityTypeName}' is not mapped to a table.");
            var property = entity.GetProperties().FirstOrDefault(p => string.Equals(p.Name, field.Field, StringComparison.OrdinalIgnoreCase));
            var column = field.SqlColumn ?? property?.GetColumnName()
                ?? throw new InvalidOperationException($"Entity '{field.EntityTypeName}' has no property '{field.Field}'.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(table + column, "^[A-Za-z0-9_]+$")) throw new InvalidOperationException("Unexpected characters in table or column name.");
#pragma warning disable EF1002
            if (field.IsTimestamp)
            {
                var clrType = Nullable.GetUnderlyingType(property?.ClrType ?? typeof(DateTime)) ?? property?.ClrType ?? typeof(DateTime);
                object moment = clrType == typeof(DateTimeOffset) ? DateTimeOffset.UtcNow.AddDays(1) : DateTime.UtcNow.AddDays(1);
                await context.Database.ExecuteSqlRawAsync($"UPDATE \"{table}\" SET \"{column}\" = {{0}}", moment);
            }
            else
                await context.Database.ExecuteSqlRawAsync($"UPDATE \"{table}\" SET \"{column}\" = \"{column}\" + 1");
#pragma warning restore EF1002
            return;
        }
        throw new InvalidOperationException($"No DbContext maps entity '{field.EntityTypeName}'.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        Client.Dispose();
        await host.StopAsync();
        host.Dispose();
        await keeper.DisposeAsync();
    }
}
