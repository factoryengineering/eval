using ApiConformance;

public class ProtocolGroupTests
{
    private static async Task<IReadOnlyList<ScenarioResult>> WhenRunningAllGroups(string[] sources, DomainDescriptor domain) =>
        await ProtocolScenarios.Run(ProtocolScenarios.All(domain), Subject.Hosts(sources), domain);

    private static string Describe(IEnumerable<ScenarioResult> results) =>
        string.Join(Environment.NewLine, results.Where(r => !r.Passed).Select(r => $"{r.Name}: {r.Failure}"));

    public static IEnumerable<object[]> Groups() =>
        from domain in new[] { "blog.json", "blog-root.json" }
        from groupName in new[] { ProtocolScenarios.CreateGroup, ProtocolScenarios.ReadGroup, ProtocolScenarios.ReplaceGroup, ProtocolScenarios.DeleteGroup, ProtocolScenarios.ServerOwnedGroup, ProtocolScenarios.RaceGroup, ProtocolScenarios.LostResponseGroup }
        where groupName != ProtocolScenarios.ServerOwnedGroup || domain == "blog.json"
        select new object[] { domain, groupName };

    [Theory]
    [MemberData(nameof(Groups))]
    public async Task GivenGoodReference_WhenRunningGroup_ThenAllScenariosPass(string domainFile, string groupName)
    {
        var domain = Subject.Domain(domainFile);
        var scenarios = ProtocolScenarios.All(domain).Where(s => s.Group == groupName).ToList();

        var results = await ProtocolScenarios.Run(scenarios, Subject.Hosts(Subject.BlogSources().Values), domain);

        Assert.NotEmpty(results);
        Assert.True(results.All(r => r.Passed), Describe(results));
    }

    public static IEnumerable<object[]> DomainFiles() => Subject.DomainFiles().Select(file => new object[] { file });

    [Theory]
    [MemberData(nameof(DomainFiles))]
    public async Task GivenGoodReference_WhenRunningAllGroups_ThenAllScenariosPass(string domainFile)
    {
        var domain = Subject.Domain(domainFile);

        var results = await WhenRunningAllGroups(Subject.Sources(Subject.SubjectOf(domainFile)).Values.ToArray(), domain);

        Assert.NotEmpty(results);
        Assert.True(results.All(r => r.Passed), Describe(results));
    }

    public static IEnumerable<object[]> MutantNames() => Subject.Mutants().Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(MutantNames))]
    public async Task GivenMutant_WhenRunningAllGroups_ThenExactlyTheExpectedScenariosFail(string name)
    {
        var mutant = Subject.Mutants().Single(m => m.Name == name);
        var sources = Subject.Apply(Subject.Sources(mutant.Domain), mutant);

        var results = await WhenRunningAllGroups(sources, Subject.Domain($"{mutant.Domain}.json"));

        var failed = results.Where(r => !r.Passed).Select(r => r.Name).Order().ToArray();
        Assert.True(failed.SequenceEqual(mutant.ExpectedFailures.Order()), $"Expected [{string.Join(", ", mutant.ExpectedFailures)}] but failed [{string.Join(", ", failed)}]." + Environment.NewLine + Describe(results));
    }
}
