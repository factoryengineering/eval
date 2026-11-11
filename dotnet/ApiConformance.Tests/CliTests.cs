using System.Diagnostics;
using System.Text.Json;
using ApiConformance;

public class CliTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static string CliPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        return Path.Combine(Directory.GetParent(Subject.EvalRoot)!.Parent!.FullName, "dotnet", "ApiConformance.Cli", "bin", configuration, framework, "ApiConformance.Cli.dll");
    }

    private static async Task<JsonElement> WhenSendingToCli(string request)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{CliPath()}\"") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        using var timeout = new CancellationTokenSource(Timeout);
        await process.StandardInput.WriteLineAsync(request);
        process.StandardInput.Close();
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.NotNull(line);
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    private static string GivenRequest(string id, string domainFile, string[] sources) =>
        JsonSerializer.Serialize(new { id, domain = JsonDocument.Parse(File.ReadAllText(Path.Combine(Subject.EvalRoot, "domains", domainFile))).RootElement, sources });

    private static IEnumerable<JsonElement> Checks(JsonElement response) => response.GetProperty("checks").EnumerateArray();

    private static string[] Failing(JsonElement response) =>
        [.. Checks(response).Where(c => !c.GetProperty("passed").GetBoolean() && !(c.TryGetProperty("advisory", out var advisory) && advisory.GetBoolean())).Select(c => $"{c.GetProperty("layer").GetString()}:{c.GetProperty("id").GetString()}").Order()];

    [Fact]
    public async Task GivenGoodReference_WhenRunningThroughTheCli_ThenEveryLayerReportsAndNothingFails()
    {
        var request = GivenRequest("good", "logistics.json", [.. Subject.Sources("logistics").Values]);

        var response = await WhenSendingToCli(request);

        Assert.Equal("good", response.GetProperty("id").GetString());
        Assert.True(response.GetProperty("compile").GetProperty("ok").GetBoolean());
        Assert.Equal([CheckReport.LintLayer, CheckReport.ScenarioLayer, CheckReport.SpecLayer, CheckReport.StaticLayer], Checks(response).Select(c => c.GetProperty("layer").GetString()!).Distinct().Order());
        Assert.Equal(OpenApiLint.Ids.Order(), Checks(response).Where(c => c.GetProperty("layer").GetString() == CheckReport.LintLayer).Select(c => c.GetProperty("id").GetString()!).Order());
        Assert.Empty(Failing(response));
    }

    [Fact]
    public async Task GivenMutant_WhenRunningThroughTheCli_ThenTheFailingChecksSpanLayers()
    {
        var mutant = Subject.Mutants().Single(m => m.Name == "openapi-put-no-404");
        var request = GivenRequest("mutant", $"{mutant.Domain}.json", Subject.Apply(Subject.Sources(mutant.Domain), mutant));

        var response = await WhenSendingToCli(request);

        Assert.Equal(["lint:put-declares-412-and-404", "spec:spec-declares-observed-statuses"], Failing(response));
    }

    [Fact]
    public async Task GivenSourcesThatDoNotCompile_WhenRunningThroughTheCli_ThenCompileErrorsAreReported()
    {
        var request = GivenRequest("broken", "blog.json", ["public class Broken { int x = ; }"]);

        var response = await WhenSendingToCli(request);

        Assert.False(response.GetProperty("compile").GetProperty("ok").GetBoolean());
        Assert.NotEmpty(response.GetProperty("compile").GetProperty("errors").EnumerateArray());
        Assert.Empty(Checks(response));
    }
}
