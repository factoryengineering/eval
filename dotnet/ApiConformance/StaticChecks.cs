using System.Reflection;

namespace ApiConformance;

public sealed record StaticCheckResult(string Id, bool Passed, string Evidence, bool Advisory = false);

public sealed record StaticInputs(IReadOnlyList<string> Sources, Assembly Assembly, ApiHost Host, DomainDescriptor Domain);

public static class StaticChecks
{
    public static readonly IReadOnlyList<string> Ids =
    [
        "public-id-unique-index", "internal-key-int-hidden", "etag-concurrency-token", "no-rowversion-etag", "creation-hash-immutable",
        "tombstone-property", "etag-column-not-null", "server-owned-not-concurrency-token",
        "routes-use-guid", "no-entity-in-responses", "no-int-id-in-dtos", "sha256-used-for-etag", "etag-header-set",
        "if-match-required", "cancellation-token-passed", "problem-details-used", "no-check-then-insert"
    ];

    public static IReadOnlyList<StaticCheckResult> Run(StaticInputs inputs)
    {
        using var facts = new ModelFacts(inputs);
        var source = new SourceFacts(inputs, facts.EntityClrNames);
        var exposure = SourceChecks.RouteProblems(source).Concat(SourceChecks.DtoProblems(source)).ToList();
        var model = ModelChecks.Run(facts).Select(r => r.Id == "internal-key-int-hidden" ? WithExposure(r, exposure) : r);
        return [.. model, .. SourceChecks.Run(source)];
    }

    private static StaticCheckResult WithExposure(StaticCheckResult result, List<string> exposure) =>
        exposure.Count == 0 ? result with { Evidence = result.Evidence + "; no integer id in routes or request and response types" }
            : result with { Passed = false, Evidence = string.Join("; ", result.Passed ? exposure : [result.Evidence, .. exposure]) };

    public static IReadOnlyList<string> Failing(IEnumerable<StaticCheckResult> results) =>
        [.. results.Where(r => !r.Passed && !r.Advisory).Select(r => r.Id).Order()];

    internal static StaticCheckResult Result(string id, IReadOnlyCollection<string> problems, string passEvidence, bool advisory = false) =>
        new(id, problems.Count == 0, problems.Count == 0 ? passEvidence : string.Join("; ", problems), advisory);
}
