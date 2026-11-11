using ApiConformance;

public class CreateGroupTests
{
    private static async Task<IReadOnlyList<ScenarioResult>> WhenRunningCreateGroup(string[] sources, DomainDescriptor domain) =>
        await ProtocolScenarios.Run(ProtocolScenarios.Create(domain), () => Subject.Start(sources), domain);

    private static string Describe(IEnumerable<ScenarioResult> results) =>
        string.Join(Environment.NewLine, results.Where(r => !r.Passed).Select(r => $"{r.Name}: {r.Failure}"));

    [Theory]
    [InlineData("blog.json")]
    [InlineData("blog-root.json")]
    public async Task GivenGoodReference_WhenRunningCreateGroup_ThenAllScenariosPass(string domainFile)
    {
        var results = await WhenRunningCreateGroup(Subject.BlogSources().Values.ToArray(), Subject.Domain(domainFile));

        Assert.NotEmpty(results);
        Assert.True(results.All(r => r.Passed), Describe(results));
    }

    public static IEnumerable<object[]> MutantNames() => Subject.Mutants().Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(MutantNames))]
    public async Task GivenMutant_WhenRunningCreateGroup_ThenExactlyTheExpectedScenariosFail(string name)
    {
        var mutant = Subject.Mutants().Single(m => m.Name == name);
        var sources = Subject.Apply(Subject.BlogSources(), mutant);

        var results = await WhenRunningCreateGroup(sources, Subject.Domain("blog.json"));

        var failed = results.Where(r => !r.Passed).Select(r => r.Name).Order().ToArray();
        Assert.True(failed.SequenceEqual(mutant.ExpectedFailures.Order()), $"Expected [{string.Join(", ", mutant.ExpectedFailures)}] but failed [{string.Join(", ", failed)}]." + Environment.NewLine + Describe(results));
    }
}

public class CanonicalHashTests
{
    private const string Id = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string ParentId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private static KeyValuePair<string, object> Field(string name, object value) => new(name, value);

    [Fact]
    public void GivenIdAndName_WhenHashing_ThenMatchesIndependentVector()
    {
        var hash = CanonicalHash.Compute([Field("id", Guid.Parse(Id)), Field("name", "gear")]);

        Assert.Equal("E8nskwEfrGHFO_eGiOO2klena6fnstJc9R7PwfQ4AJI", hash);
    }

    [Fact]
    public void GivenEscapesAndDecomposedLetters_WhenHashing_ThenNfcAndEscapingMatchIndependentVector()
    {
        var decomposed = "cafe\u0301 \"q\" \\ \n \u0001";

        var hash = CanonicalHash.Compute([Field("id", Guid.Parse(Id)), Field("blogId", Guid.Parse(ParentId)), Field("title", decomposed)]);

        Assert.Equal("h-Pwti8BwsQZJPGsLfDEe0NRp2ZWZ7GvYmy1WllIxj0", hash);
    }

    [Fact]
    public void GivenComposedAndDecomposedStrings_WhenHashing_ThenHashesAreEqual()
    {
        var composed = CanonicalHash.Compute([Field("name", "caf\u00e9")]);
        var decomposed = CanonicalHash.Compute([Field("name", "cafe\u0301")]);

        Assert.Equal(composed, decomposed);
    }

    [Fact]
    public void GivenNumberAndBoolean_WhenHashing_ThenMatchesIndependentVector()
    {
        var hash = CanonicalHash.Compute([Field("id", Guid.Parse(Id)), Field("title", "x"), Field("count", 5), Field("flag", true)]);

        Assert.Equal("fzZhOLggcOoRasGQT9QUZRIFgr0oicWY-siiZA_f4rY", hash);
    }

    [Fact]
    public void GivenSwappedFieldOrder_WhenHashing_ThenMatchesDifferentIndependentVector()
    {
        var hash = CanonicalHash.Compute([Field("name", "gear"), Field("id", Guid.Parse(Id))]);

        Assert.Equal("VwiNCmWRaoKcin7fMDptRWmqMYUo7Npe3pMO4TmYpRQ", hash);
        Assert.NotEqual("E8nskwEfrGHFO_eGiOO2klena6fnstJc9R7PwfQ4AJI", hash);
    }

    [Fact]
    public void GivenUppercaseGuid_WhenCanonicalizing_ThenLowercaseDForm()
    {
        var json = CanonicalHash.Json([Field("id", Guid.Parse(Id.ToUpperInvariant()))]);

        Assert.Equal($$"""{"id":"{{Id}}"}""", json);
    }
}
