using EvalRunner;

public class TestRunnerTests
{
    const string PostQueriesSubject = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using EfFixtures;
        using Microsoft.EntityFrameworkCore;

        public static class PostQueries
        {
            public static async Task<List<string>> Popular(BlogContext database, int minViews, CancellationToken cancellationToken)
            {
                var titlesQuery =
                    from post in database.Posts
                    where post.Views > minViews
                    orderby post.Views descending
                    select post.Title;
                return await titlesQuery.ToListAsync(cancellationToken);
            }
        }
        """;

    static readonly Mutant BoundaryMutant = new("boundary", "post.Views > minViews", "post.Views >= minViews");

    const string SeedingHeader = """
        public class PopularTests
        {
            static async Task<(SqliteConnection, BlogContext)> GivenPosts(params (string Title, int Views)[] posts)
            {
                var connection = new SqliteConnection("DataSource=:memory:");
                await connection.OpenAsync();
                var database = new BlogContext(new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options);
                await database.Database.EnsureCreatedAsync();
                database.Blogs.Add(new Blog { Name = "b", Posts = [.. posts.Select(post => new Post { Title = post.Title, Views = post.Views })] });
                await database.SaveChangesAsync();
                return (connection, database);
            }
        """;

    static Task<TestResult> WhenRunningTests(string code, params Mutant[] mutants) =>
        SampleRunner.RunTests(new Request("t", code, Subject: PostQueriesSubject, Mutants: [.. mutants]));

    [Fact]
    public async Task GivenPassingTest_WhenRunning_ThenItIsReportedPassed()
    {
        var result = await WhenRunningTests("""
            public class T { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }
            """);

        Assert.True(result.Compiled);
        var outcome = Assert.Single(result.Tests);
        Assert.True(outcome.Passed);
    }

    [Fact]
    public async Task GivenFailingTest_WhenRunning_ThenTheAssertionMessageIsReported()
    {
        var result = await WhenRunningTests("""
            public class T { [Fact] public void Adds() => Assert.Equal(3, 1 + 1); }
            """);

        var outcome = Assert.Single(result.Tests);
        Assert.False(outcome.Passed);
        Assert.Contains("EqualException", outcome.Error);
    }

    [Fact]
    public async Task GivenCompileError_WhenRunning_ThenDiagnosticsAreReported()
    {
        var result = await WhenRunningTests("public class T { [Fact] public void Bad() { int x = \"s\"; } }");

        Assert.False(result.Compiled);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.StartsWith("CS0029"));
    }

    [Fact]
    public async Task GivenTheoryWithInlineData_WhenRunning_ThenEachRowIsOneOutcome()
    {
        var result = await WhenRunningTests("""
            public class T
            {
                [Theory]
                [InlineData(1, 2)]
                [InlineData(2, 3)]
                public void Increments(int before, int after) => Assert.Equal(after, before + 1);
            }
            """);

        Assert.Equal(2, result.Tests.Count);
        Assert.All(result.Tests, outcome => Assert.True(outcome.Passed));
    }

    [Fact]
    public async Task GivenClassFixture_WhenRunning_ThenItIsInitializedAndInjected()
    {
        var result = await WhenRunningTests("""
            public class Counter : IAsyncLifetime
            {
                public int Value;
                public Task InitializeAsync() { Value = 41; return Task.CompletedTask; }
                public Task DisposeAsync() => Task.CompletedTask;
            }
            public class T : IClassFixture<Counter>
            {
                readonly Counter counter;
                public T(Counter counter) { this.counter = counter; }
                [Fact] public void Starts() => Assert.Equal(41, counter.Value);
            }
            """);

        var outcome = Assert.Single(result.Tests);
        Assert.True(outcome.Passed, outcome.Error);
    }

    [Fact]
    public async Task GivenTestsUsingForbiddenApi_WhenRunning_ThenTheyAreNotExecuted()
    {
        var result = await WhenRunningTests("""
            public class T { [Fact] public void Reads() => System.IO.File.ReadAllText("/etc/hosts"); }
            """);

        Assert.Contains(result.Forbidden, name => name.StartsWith("System.IO"));
        Assert.Empty(result.Tests);
    }

    [Fact]
    public async Task GivenBoundaryTest_WhenMutatingTheComparison_ThenTheMutantIsKilled()
    {
        var result = await WhenRunningTests(SeedingHeader + """

                [Fact]
                public async Task GivenPostsAtTheThreshold_WhenFilteringPopular_ThenThePostAtTheThresholdIsExcluded()
                {
                    var (connection, database) = await GivenPosts(("exact", 100), ("above", 101));
                    using var _ = connection;
                    using var __ = database;

                    var titles = await PostQueries.Popular(database, 100, CancellationToken.None);

                    Assert.Equal(["above"], titles);
                }
            }
            """, BoundaryMutant);

        Assert.All(result.Tests, outcome => Assert.True(outcome.Passed, outcome.Error));
        var mutant = Assert.Single(result.Mutants);
        Assert.Equal("killed", mutant.Status);
    }

    [Fact]
    public async Task GivenWeakTest_WhenMutatingTheComparison_ThenTheMutantSurvives()
    {
        var result = await WhenRunningTests(SeedingHeader + """

                [Fact]
                public async Task GivenOnePopularPost_WhenFilteringPopular_ThenItIsReturned()
                {
                    var (connection, database) = await GivenPosts(("big", 500));
                    using var _ = connection;
                    using var __ = database;

                    var titles = await PostQueries.Popular(database, 100, CancellationToken.None);

                    Assert.Equal(["big"], titles);
                }
            }
            """, BoundaryMutant);

        var mutant = Assert.Single(result.Mutants);
        Assert.Equal("survived", mutant.Status);
    }

    [Fact]
    public async Task GivenMutantThatDoesNotApply_WhenMutating_ThenItIsReportedInvalid()
    {
        var result = await WhenRunningTests(
            "public class T { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }",
            new Mutant("missing", "text that is not there", "x"));

        Assert.Equal("invalid", Assert.Single(result.Mutants).Status);
    }

    [Fact]
    public async Task GivenTestThatUsesHelperAndDiscards_WhenRunning_ThenFactsDescribeTheStructure()
    {
        var result = await WhenRunningTests(SeedingHeader + """

                [Fact]
                public async Task GivenOnePost_WhenFilteringPopular_ThenItIsReturned()
                {
                    var (connection, database) = await GivenPosts(("big", 500));
                    using var _ = connection;
                    using var __ = database;

                    var titles = await PostQueries.Popular(database, 100, CancellationToken.None);

                    Assert.Equal(["big"], titles);
                }
            }
            """);

        var facts = result.Facts!;
        Assert.Equal(["GivenOnePost_WhenFilteringPopular_ThenItIsReturned"], facts.TestMethods);
        Assert.Equal(["GivenPosts"], facts.HelperMethods);
        Assert.Equal(2, facts.DiscardUsings);
        Assert.Equal(0, facts.ConnectionsOpenedInTests);
    }

    [Fact]
    public async Task GivenTestThatOpensItsOwnConnection_WhenRunning_ThenTheConnectionIsCounted()
    {
        var result = await WhenRunningTests("""
            public class T
            {
                [Fact]
                public void Opens()
                {
                    using var connection = new SqliteConnection("DataSource=:memory:");
                    connection.Open();
                }
            }
            """);

        Assert.Equal(1, result.Facts!.ConnectionsOpenedInTests);
    }

    [Fact]
    public async Task GivenFailingTests_WhenMutating_ThenMutantsAreNotEvaluated()
    {
        var result = await WhenRunningTests(
            "public class T { [Fact] public void Fails() => Assert.True(false); }", BoundaryMutant);

        Assert.Empty(result.Mutants);
    }
}
