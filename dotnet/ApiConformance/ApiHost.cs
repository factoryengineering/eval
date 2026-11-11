using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiConformance;

public sealed class ApiHost : IAsyncDisposable
{
    private readonly IHost host;
    private readonly SqliteConnection keeper;
    private readonly string databasePath;
    private bool disposed;

    private ApiHost(IHost host, SqliteConnection keeper, string databasePath, HttpClient client, string connectionString, IReadOnlyList<Type> dbContextTypes, Interleaver interleaver)
    {
        Interleaver = interleaver;
        this.host = host;
        this.keeper = keeper;
        this.databasePath = databasePath;
        Client = client;
        ConnectionString = connectionString;
        DbContextTypes = dbContextTypes;
    }

    public HttpClient Client { get; }
    public Interleaver Interleaver { get; }
    public HttpMessageHandler CreateHandler() => host.GetTestServer().CreateHandler();
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

        var path = Path.Combine(Path.GetTempPath(), $"apiconformance-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False;Default Timeout=30";
        var keeper = new SqliteConnection(connectionString);
        keeper.Open();
        using (var pragma = keeper.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL";
            pragma.ExecuteNonQuery();
        }
        var interleaver = new Interleaver();
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
                        foreach (var type in generated.GetTypes().Where(t => typeof(DbContext).IsAssignableFrom(t) && !t.IsAbstract))
                            ConfigureInterceptors.MakeGenericMethod(type).Invoke(null, [services, interleaver]);
                    })
                    .Configure(app =>
                    {
                        app.Use(next => async context =>
                        {
                            try
                            {
                                await next(context);
                            }
                            catch (Exception failure) when (!context.Response.HasStarted)
                            {
                                context.Response.StatusCode = 500;
                                await context.Response.WriteAsync($"Unhandled {failure.GetType().Name}: {failure.Message}");
                            }
                        });
                        app.Use(interleaver.Middleware);
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    }))
                .Build();
            var contextTypes = FindDbContextTypes(generated, host.Services.GetRequiredService<IServiceProviderIsService>());
            using (var scope = host.Services.CreateScope())
                foreach (var type in contextTypes)
                    ((DbContext)scope.ServiceProvider.GetRequiredService(type)).Database.EnsureCreated();
            host.Start();
            return new ApiHost(host, keeper, path, host.GetTestClient(), connectionString, contextTypes, interleaver);
        }
        catch
        {
            host?.Dispose();
            keeper.Dispose();
            DeleteDatabase(path);
            throw;
        }
    }

    private static readonly MethodInfo ConfigureInterceptors = typeof(ApiHost).GetMethod(nameof(AddInterceptors), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void AddInterceptors<TContext>(IServiceCollection services, Interleaver interleaver) where TContext : DbContext =>
        services.ConfigureDbContext<TContext>(options => options.AddInterceptors(interleaver.CommandInterceptor, interleaver.TransactionInterceptor));

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

    public async Task<int> CountRowsHoldingGuid(Guid id)
    {
        using var scope = host.Services.CreateScope();
        var total = 0;
        foreach (var type in DbContextTypes)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(type);
            foreach (var entity in context.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (table is null) continue;
                foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(Guid) || p.ClrType == typeof(Guid?)))
                {
                    using var command = context.Database.GetDbConnection().CreateCommand();
                    command.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE upper(\"{property.GetColumnName()}\") = upper($id)";
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "$id";
                    parameter.Value = id.ToString("D");
                    command.Parameters.Add(parameter);
                    await context.Database.OpenConnectionAsync();
                    total += Convert.ToInt32(await command.ExecuteScalarAsync());
                }
            }
        }
        return total;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        Client.Dispose();
        await host.StopAsync();
        host.Dispose();
        await keeper.DisposeAsync();
        DeleteDatabase(databasePath);
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
    }
}
