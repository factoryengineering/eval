namespace ApiConformance;

public sealed record CheckEntry(string Layer, string Id, bool Passed, string Evidence, string? Group = null, bool Advisory = false);

public sealed record CheckReportResult(bool Compiled, IReadOnlyList<string> CompileErrors, IReadOnlyList<CheckEntry> Checks)
{
    public IEnumerable<CheckEntry> Failing => Checks.Where(c => !c.Passed && !c.Advisory);
}

public static class CheckReport
{
    public const string ScenarioLayer = "scenario";
    public const string StaticLayer = "static";
    public const string LintLayer = "lint";
    public const string SpecLayer = "spec";
    public const string HostLayer = "host";

    public static async Task<CheckReportResult> Run(IReadOnlyList<string> sources, DomainDescriptor domain)
    {
        var compiled = GeneratedApp.Compile(sources);
        if (compiled.Assembly is null) return new(false, [.. compiled.Errors.Select(e => e.ToString())], []);

        ApiHost host;
        try { host = compiled.Host(); }
        catch (Exception failure) { return new(true, [], [new(HostLayer, "host-starts", false, failure.InnerException?.Message ?? failure.Message)]); }

        await using (host)
        {
            var scenarios = await ProtocolScenarios.Run(ProtocolScenarios.All(domain), compiled.Host, domain);
            var document = await host.GetOpenApiDocument();
            var checks = new List<CheckEntry>();
            checks.AddRange(scenarios.Select(s => new CheckEntry(ScenarioLayer, s.Name, s.Passed, s.Failure ?? "ok", s.Group)));
            checks.AddRange(StaticChecks.Run(new StaticInputs(sources, compiled.Assembly, host, domain)).Select(r => new CheckEntry(StaticLayer, r.Id, r.Passed, r.Evidence, Advisory: r.Advisory)));
            checks.AddRange(OpenApiLint.Run(document, domain).Select(r => new CheckEntry(LintLayer, r.Id, r.Passed, r.Evidence)));
            checks.AddRange(SpecConformance.Run(document, scenarios.SelectMany(s => s.Traces ?? [])).Select(r => new CheckEntry(SpecLayer, r.Id, r.Passed, r.Evidence)));
            return new(true, [], checks);
        }
    }
}
