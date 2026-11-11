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

    private static Mutant GivenRefactoring(string name, string find, string replace, params Edit[] extra) => new(name, "Controllers.cs", find, replace, [], Extra: extra);

    [Fact]
    public async Task GivenControllerThatWritesTheETagInAPrivateHelper_WhenCheckingStatically_ThenEveryCheckStillPasses()
    {
        var helper = "private void SetETag(string etag) => Response.Headers.ETag = $\"\\\"{etag}\\\"\";";
        var mutant = GivenRefactoring("etag-helper", "Response.Headers.ETag = $\"\\\"{stored.ETag}\\\"\";", "SetETag(stored.ETag);",
            new Edit("Controllers.cs", "Response.Headers.ETag = $\"\\\"{eTag}\\\"\";", "SetETag(eTag);"),
            new Edit("Controllers.cs", "public BlogsController(BlogService blogs) => this.blogs = blogs;", "public BlogsController(BlogService blogs) => this.blogs = blogs;\n    " + helper),
            new Edit("Controllers.cs", "public PostsController(PostService posts) => this.posts = posts;", "public PostsController(PostService posts) => this.posts = posts;\n    " + helper));

        var results = await WhenCheckingStatically(Subject.Sources("blog"), mutant, Subject.Domain("blog.json"));

        Assert.True(results.All(r => r.Passed), Describe(results));
    }

    [Fact]
    public async Task GivenControllersThatWriteTheETagInAHelperOfAnAbstractBaseClass_WhenCheckingStatically_ThenEveryCheckStillPasses()
    {
        var baseClass = "public abstract class ApiBase : ControllerBase\n{\n    protected void SetETag(string etag) => Response.Headers.ETag = $\"\\\"{etag}\\\"\";\n}\n\n[ApiController]\n[Route(\"api/blogs\")]\npublic class BlogsController : ControllerBase";
        var mutant = GivenRefactoring("etag-base-helper", "Response.Headers.ETag = $\"\\\"{stored.ETag}\\\"\";", "SetETag(stored.ETag);",
            new Edit("Controllers.cs", "Response.Headers.ETag = $\"\\\"{eTag}\\\"\";", "SetETag(eTag);"),
            new Edit("Controllers.cs", "[ApiController]\n[Route(\"api/blogs\")]\npublic class BlogsController : ControllerBase", baseClass.Replace("public class BlogsController : ControllerBase", "public class BlogsController : ApiBase")),
            new Edit("Controllers.cs", "public class PostsController : ControllerBase", "public class PostsController : ApiBase"));

        var results = await WhenCheckingStatically(Subject.Sources("blog"), mutant, Subject.Domain("blog.json"));

        Assert.True(results.All(r => r.Passed), Describe(results));
    }

    [Fact]
    public async Task GivenControllerHelperThatTakesAnEntity_WhenCheckingStatically_ThenItIsNotCountedAsAResponse()
    {
        var mutant = GivenRefactoring("entity-helper", "var stored = await blogs.Create(body, cancellationToken);",
            "var stored = await blogs.Create(body, cancellationToken);\n        _ = Describe(new Blog());",
            new Edit("Controllers.cs", "public BlogsController(BlogService blogs) => this.blogs = blogs;", "public BlogsController(BlogService blogs) => this.blogs = blogs;\n    private static string Describe(Blog blog) => \"\";"));

        var results = await WhenCheckingStatically(Subject.Sources("blog"), mutant, Subject.Domain("blog.json"));

        Assert.True(results.Single(r => r.Id == "no-entity-in-responses").Passed, Describe(results));
    }

    [Fact]
    public async Task GivenControllerThatReturnsAnEntityThroughOk_WhenCheckingStatically_ThenItIsStillFlagged()
    {
        var mutant = GivenRefactoring("entity-returned", "var stored = await blogs.Create(body, cancellationToken);",
            "var stored = await blogs.Create(body, cancellationToken);\n        if (stored.ETag == \"\") return Ok(new Blog());");

        var results = await WhenCheckingStatically(Subject.Sources("blog"), mutant, Subject.Domain("blog.json"));

        Assert.False(results.Single(r => r.Id == "no-entity-in-responses").Passed, Describe(results));
    }
}
