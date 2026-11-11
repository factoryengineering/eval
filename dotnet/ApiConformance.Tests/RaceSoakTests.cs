using ApiConformance;

public class RaceSoakTests
{
    private const int Repetitions = 20;

    [SoakFact]
    [Trait("Category", "Soak")]
    public async Task GivenGoodReference_WhenRunningRaceAndLostResponseGroupsRepeatedly_ThenEveryRunPasses()
    {
        var failures = new List<string>();
        foreach (var domainFile in Subject.DomainFiles())
        {
            var domain = Subject.Domain(domainFile);
            var hosts = Subject.Hosts(Subject.Sources(Subject.SubjectOf(domainFile)).Values);
            var scenarios = ProtocolScenarios.Race(domain).Concat(ProtocolScenarios.LostResponse(domain)).ToList();
            for (var run = 1; run <= Repetitions; run++)
            {
                var results = await ProtocolScenarios.Run(scenarios, hosts, domain);
                failures.AddRange(results.Where(r => !r.Passed).Select(r => $"{domainFile} run {run} {r.Name}: {r.Failure}"));
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [SoakFact]
    [Trait("Category", "Soak")]
    public async Task GivenMutant_WhenRunningRaceGroupRepeatedly_ThenTheSameScenariosFailEveryTime()
    {
        var unstable = new List<string>();
        foreach (var mutant in Subject.Mutants())
        {
            var domain = Subject.Domain($"{mutant.Domain}.json");
            var hosts = Subject.Hosts(Subject.Apply(Subject.Sources(mutant.Domain), mutant));
            var scenarios = ProtocolScenarios.Race(domain).ToList();
            var expected = scenarios.Select(s => s.Name).Intersect(mutant.ExpectedFailures).Order().ToArray();
            for (var run = 1; run <= Repetitions; run++)
            {
                var results = await ProtocolScenarios.Run(scenarios, hosts, domain);
                var failed = results.Where(r => !r.Passed).Select(r => r.Name).Order().ToArray();
                if (!failed.SequenceEqual(expected)) unstable.Add($"{mutant.Name} run {run}: expected [{string.Join(", ", expected)}] got [{string.Join(", ", failed)}]");
            }
        }

        Assert.True(unstable.Count == 0, string.Join(Environment.NewLine, unstable));
    }
}
