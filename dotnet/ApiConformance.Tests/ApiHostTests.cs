using System.Net;
using System.Data;
using ApiConformance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class ApiHostTests : IAsyncLifetime
{
    private readonly List<ApiHost> hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in hosts) await host.DisposeAsync();
    }

    private ApiHost GivenHostStarted(string source = WidgetApp.Source)
    {
        var host = GeneratedApp.Compile(source).Host();
        hosts.Add(host);
        return host;
    }

    private static Task<HttpTrace> WhenGet(ApiHost host, Guid id) =>
        HttpTrace.Send(host.Client, HttpMethod.Get, $"/api/widgets/{id}");

    private static Task<HttpTrace> WhenPost(ApiHost host, Guid id) =>
        HttpTrace.Send(host.Client, HttpMethod.Post, "/api/widgets", $$"""{"id":"{{id}}","name":"gear"}""");

    private static void ThenStatus(HttpStatusCode expected, HttpTrace trace) =>
        Assert.True(trace.Status == expected, trace.ToString());

    [Fact]
    public async Task GivenCompiledApp_WhenGettingMissingWidget_ThenNotFound()
    {
        var host = GivenHostStarted();

        var trace = await WhenGet(host, Guid.NewGuid());

        ThenStatus(HttpStatusCode.NotFound, trace);
    }

    [Fact]
    public async Task GivenCompiledApp_WhenPostingWidget_ThenCreatedWithLocation()
    {
        var host = GivenHostStarted();
        var id = Guid.NewGuid();

        var trace = await WhenPost(host, id);

        ThenStatus(HttpStatusCode.Created, trace);
        Assert.Equal($"/api/widgets/{id}", trace.Header("Location"));
        Assert.Contains("gear", trace.ResponseBody);
    }

    [Fact]
    public void GivenSourceWithCompileError_WhenCompiling_ThenDiagnosticsAreReported()
    {
        var result = GeneratedApp.Compile(WidgetApp.Broken);

        Assert.False(result.Success);
        Assert.Null(result.Assembly);
        var error = Assert.Single(result.Errors);
        Assert.Equal("CS0246", error.Id);
        Assert.Contains("Missing", error.Message);
        Assert.StartsWith("Generated0.cs", error.Location);
    }

    [Fact]
    public void GivenFailedCompilation_WhenHosting_ThenExplainsWithDiagnostics()
    {
        var result = GeneratedApp.Compile(WidgetApp.Broken);

        var failure = Assert.Throws<InvalidOperationException>(() => result.Host());

        Assert.Contains("CS0246", failure.Message);
    }

    [Fact]
    public void GivenControllerInGeneratedAssembly_WhenHosting_ThenControllerIsDiscovered()
    {
        var host = GivenHostStarted();

        var manager = host.Services.GetRequiredService<Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPartManager>();
        var feature = new Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature();
        manager.PopulateFeature(feature);

        Assert.Contains(feature.Controllers, t => t.Name == "WidgetsController");
        Assert.Equal(typeof(DbContext).Name, host.DbContextTypes.Single().BaseType!.Name);
    }

    [Fact]
    public async Task GivenWidgetPostedToOneHost_WhenReadingFromAnotherHost_ThenNotFound()
    {
        var first = GivenHostStarted();
        var second = GivenHostStarted();
        var id = Guid.NewGuid();
        await WhenPost(first, id);

        var onFirst = await WhenGet(first, id);
        var onSecond = await WhenGet(second, id);

        ThenStatus(HttpStatusCode.OK, onFirst);
        ThenStatus(HttpStatusCode.NotFound, onSecond);
        Assert.NotEqual(first.ConnectionString, second.ConnectionString);
    }

    [Fact]
    public async Task GivenRunningHost_WhenDisposed_ThenDatabaseClosedAndClientRejected()
    {
        var host = GivenHostStarted();
        Assert.Equal(ConnectionState.Open, host.DatabaseState);

        await host.DisposeAsync();

        Assert.True(host.IsDisposed);
        Assert.Equal(ConnectionState.Closed, host.DatabaseState);
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => WhenGet(host, Guid.NewGuid()));
    }
}
