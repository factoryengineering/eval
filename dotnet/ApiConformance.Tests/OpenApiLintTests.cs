using ApiConformance;

public class OpenApiLintTests
{
    private static async Task<IReadOnlyList<LintResult>> WhenLinting(IReadOnlyDictionary<string, string> files, Mutant? mutant, DomainDescriptor domain)
    {
        var sources = mutant is null ? files.Values.ToArray() : Subject.Apply(files, mutant);
        await using var host = GeneratedApp.Compile(sources).Host();
        return OpenApiLint.Run(await host.GetOpenApiDocument(), domain);
    }

    private static string Describe(IEnumerable<LintResult> results) =>
        string.Join(Environment.NewLine, results.Select(r => $"{(r.Passed ? "pass" : "FAIL")} {r.Id}: {r.Evidence}"));

    public static IEnumerable<object[]> DomainFiles() => Subject.DomainFiles().Select(file => new object[] { file });

    [Theory]
    [MemberData(nameof(DomainFiles))]
    public async Task GivenGoodReference_WhenLinting_ThenEveryRulePasses(string domainFile)
    {
        var results = await WhenLinting(Subject.Sources(Subject.SubjectOf(domainFile)), null, Subject.Domain(domainFile));

        Assert.Equal(OpenApiLint.Ids.Order(), results.Select(r => r.Id).Order());
        Assert.True(results.All(r => r.Passed), Describe(results));
    }

    public static IEnumerable<object[]> MutantNames() => Subject.Mutants().Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(MutantNames))]
    public async Task GivenMutant_WhenLinting_ThenExactlyTheExpectedRulesFail(string name)
    {
        var mutant = Subject.Mutants().Single(m => m.Name == name);

        var results = await WhenLinting(Subject.Sources(mutant.Domain), mutant, Subject.Domain($"{mutant.Domain}.json"));

        var failed = OpenApiLint.Failing(results);
        var expected = (mutant.LintExpectedFailures ?? []).Order().ToArray();
        Assert.True(failed.SequenceEqual(expected), $"Expected [{string.Join(", ", expected)}] but failed [{string.Join(", ", failed)}]." + Environment.NewLine + Describe(results));
    }

    private const string DocumentOnlyRule = "operation-ids-unique";

    [Fact]
    public async Task GivenCraftedDocumentWithDuplicateOperationIds_WhenLinting_ThenOnlyOperationIdsUniqueFails()
    {
        await using var host = Subject.Hosts(Subject.Sources("support").Values)();
        var document = (await host.GetOpenApiDocument()).Json.Replace("\"responses\"", "\"operationId\": \"same\", \"responses\"");

        var results = OpenApiLint.Run(OpenApiDoc.Parse(document), Subject.Domain("support.json"));

        Assert.Equal([DocumentOnlyRule], OpenApiLint.Failing(results));
    }

    [Fact]
    public void GivenEveryRule_WhenLookingForMutants_ThenEachRuleHasAMutantFailingExactlyIt()
    {
        var exact = Subject.Mutants().Where(m => m.LintExpectedFailures is { Length: 1 }).Select(m => m.LintExpectedFailures![0]).ToHashSet();

        var uncovered = OpenApiLint.Ids.Where(id => !exact.Contains(id) && id != DocumentOnlyRule).ToList();
        Assert.True(uncovered.Count == 0, $"No mutant fails exactly: {string.Join(", ", uncovered)}");
    }
}
