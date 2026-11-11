using ApiConformance;

public class SpecConformanceTests
{
    private static async Task<IReadOnlyList<SpecResult>> WhenCheckingConformance(string key, IReadOnlyDictionary<string, string> files, Mutant? mutant, DomainDescriptor domain)
    {
        var sources = mutant is null ? files.Values.ToArray() : Subject.Apply(files, mutant);
        var compiled = GeneratedApp.Compile(sources);
        var results = await Subject.Scenarios(key, sources, domain);
        await using var host = compiled.Host();
        return SpecConformance.Run(await host.GetOpenApiDocument(), results.SelectMany(r => r.Traces ?? []));
    }

    private static string Describe(IEnumerable<SpecResult> results) =>
        string.Join(Environment.NewLine, results.Select(r => $"{(r.Passed ? "pass" : "FAIL")} {r.Id}: {r.Evidence}"));

    public static IEnumerable<object[]> DomainFiles() => Subject.DomainFiles().Select(file => new object[] { file });

    [Theory]
    [MemberData(nameof(DomainFiles))]
    public async Task GivenGoodReference_WhenCheckingConformance_ThenEveryRecordedExchangeMatchesTheSpec(string domainFile)
    {
        var results = await WhenCheckingConformance($"good:{domainFile}", Subject.Sources(Subject.SubjectOf(domainFile)), null, Subject.Domain(domainFile));

        Assert.Equal(SpecConformance.Ids.Order(), results.Select(r => r.Id).Order());
        Assert.True(results.All(r => r.Passed), Describe(results));
    }

    public static IEnumerable<object[]> MutantNames() => Subject.Mutants().Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(MutantNames))]
    public async Task GivenMutant_WhenCheckingConformance_ThenExactlyTheExpectedChecksFail(string name)
    {
        var mutant = Subject.Mutants().Single(m => m.Name == name);

        var results = await WhenCheckingConformance($"mutant:{name}", Subject.Sources(mutant.Domain), mutant, Subject.Domain($"{mutant.Domain}.json"));

        var failed = SpecConformance.Failing(results);
        var expected = (mutant.SpecExpectedFailures ?? []).Order().ToArray();
        Assert.True(failed.SequenceEqual(expected), $"Expected [{string.Join(", ", expected)}] but failed [{string.Join(", ", failed)}]." + Environment.NewLine + Describe(results));
    }

    [Fact]
    public void GivenSpecMutants_WhenCounting_ThenThereAreAtLeastFourAcrossTwoDomains()
    {
        var spec = Subject.Mutants().Where(m => m.SpecExpectedFailures is { Length: > 0 }).ToList();

        Assert.True(spec.Count >= 4);
        Assert.True(spec.Select(m => m.Domain).Distinct().Count() >= 2);
    }
}
