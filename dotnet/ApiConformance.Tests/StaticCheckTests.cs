using ApiConformance;

public class StaticCheckTests
{
    private static async Task<IReadOnlyList<StaticCheckResult>> WhenCheckingStatically(IReadOnlyDictionary<string, string> files, Mutant? mutant, DomainDescriptor domain)
    {
        var sources = mutant is null ? files.Values.ToArray() : Subject.Apply(files, mutant);
        var compiled = GeneratedApp.Compile(sources);
        await using var host = compiled.Host();
        return StaticChecks.Run(new StaticInputs(sources, compiled.Assembly!, host, domain));
    }

    private static string Describe(IEnumerable<StaticCheckResult> results) =>
        string.Join(Environment.NewLine, results.Select(r => $"{(r.Passed ? "pass" : "FAIL")}{(r.Advisory ? " (advisory)" : "")} {r.Id}: {r.Evidence}"));

    public static IEnumerable<object[]> DomainFiles() => Subject.DomainFiles().Select(file => new object[] { file });

    [Theory]
    [MemberData(nameof(DomainFiles))]
    public async Task GivenGoodReference_WhenCheckingStatically_ThenEveryCheckPasses(string domainFile)
    {
        var results = await WhenCheckingStatically(Subject.Sources(Subject.SubjectOf(domainFile)), null, Subject.Domain(domainFile));

        Assert.Equal(StaticChecks.Ids.Order(), results.Select(r => r.Id).Order());
        Assert.True(results.All(r => r.Passed), Describe(results));
    }

    public static IEnumerable<object[]> MutantNames() => Subject.Mutants().Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(MutantNames))]
    public async Task GivenMutant_WhenCheckingStatically_ThenExactlyTheExpectedChecksFail(string name)
    {
        var mutant = Subject.Mutants().Single(m => m.Name == name);

        var results = await WhenCheckingStatically(Subject.Sources(mutant.Domain), mutant, Subject.Domain($"{mutant.Domain}.json"));

        var failed = StaticChecks.Failing(results);
        var expected = (mutant.StaticExpectedFailures ?? []).Order().ToArray();
        Assert.True(failed.SequenceEqual(expected), $"Expected [{string.Join(", ", expected)}] but failed [{string.Join(", ", failed)}]." + Environment.NewLine + Describe(results));
    }

    public static IEnumerable<object[]> CheckThenInsertMutants() => new[] { "check-then-insert", "logistics-check-then-insert" }.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(CheckThenInsertMutants))]
    public async Task GivenCheckThenInsertMutant_WhenCheckingStatically_ThenTheAdvisoryHeuristicFlagsIt(string name)
    {
        var mutant = Subject.Mutants().Single(m => m.Name == name);

        var results = await WhenCheckingStatically(Subject.Sources(mutant.Domain), mutant, Subject.Domain($"{mutant.Domain}.json"));

        var advisory = results.Single(r => r.Id == "no-check-then-insert");
        Assert.True(advisory.Advisory && !advisory.Passed, Describe(results));
        Assert.Empty(StaticChecks.Failing(results));
    }
}
