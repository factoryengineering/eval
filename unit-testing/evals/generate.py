import json

RULES = ["given-when-then", "gherkin-helpers", "no-discard-usings", "no-connection-in-tests"]
MODEL = ("The entities are: Blog { int Id; required string Name; List<Post> Posts; List<Contributor> Contributors }, "
         "Post { int Id; required string Title; int Views; int BlogId; Blog Blog }, Contributor { int Id; required string Name; int BlogId; Blog Blog }. "
         "BlogContext has a public constructor taking DbContextOptions<BlogContext> and exposes DbSet properties Blogs, Posts and Contributors. ")
COMMON = (MODEL + "BlogContext, Blog, Post and Contributor already exist in the imported namespace EfFixtures, and so do BlogQueries and BlogTotal, "
          "so do not redefine them; define any other types or helpers you need. The only packages available are xUnit and EF Core with its SQLite provider (Microsoft.Data.Sqlite, UseSqlite); the InMemory provider is not available. Return only a C# code block.\n\nThe code under test:\n\n{{subject}}")

cases = [
    {
        "id": "popular-titles",
        "prompt": "Write xUnit tests for `BlogQueries.PopularTitles`. " + COMMON,
        "targets": [],
        "tests": {
            "subject": "unit-testing/evals/subjects/BlogQueries.cs",
            "minTests": 1,
            "rules": RULES,
            "mutants": [
                {"name": "boundary", "find": "post.Views > minViews", "replace": "post.Views >= minViews"},
                {"name": "no-filter", "find": "post.Views > minViews", "replace": "true"},
                {"name": "ascending", "find": "orderby post.Views descending", "replace": "orderby post.Views"},
            ],
        },
        "must": [],
    },
    {
        "id": "total-views-per-blog",
        "prompt": "Write xUnit tests for `BlogQueries.TotalViewsPerBlog`. " + COMMON,
        "targets": [],
        "tests": {
            "subject": "unit-testing/evals/subjects/BlogQueries.cs",
            "minTests": 1,
            "rules": RULES,
            "mutants": [
                {"name": "count-not-sum", "find": "blog.Posts.Sum(post => post.Views)", "replace": "blog.Posts.Count()"},
                {"name": "descending-order", "find": "orderby blog.Name", "replace": "orderby blog.Name descending"},
                {"name": "first-post-only", "find": "blog.Posts.Sum(post => post.Views)", "replace": "blog.Posts.OrderBy(post => post.Id).Select(post => post.Views).FirstOrDefault()"},
            ],
        },
        "must": [],
    },
]

LIFETIME = '''public class {cls} : IAsyncLifetime
{{
    SqliteConnection connection = null!;
    BlogContext database = null!;

    public async Task InitializeAsync()
    {{
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        database = new BlogContext(new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
    }}

    public async Task DisposeAsync()
    {{
        await database.DisposeAsync();
        await connection.DisposeAsync();
    }}
'''

popular_good = LIFETIME.format(cls="PopularTitlesTests") + '''
    async Task GivenPosts(params (string Title, int Views)[] posts)
    {
        database.Blogs.Add(new Blog { Name = "blog", Posts = [.. posts.Select(post => new Post { Title = post.Title, Views = post.Views })] });
        await database.SaveChangesAsync();
    }

    [Fact]
    public async Task GivenPostsAroundTheThreshold_WhenFilteringPopular_ThenOnlyPostsAboveItAreReturned()
    {
        await GivenPosts(("below", 99), ("exact", 100), ("above", 101));

        var titles = await BlogQueries.PopularTitles(database, 100, CancellationToken.None);

        Assert.Equal(["above"], titles);
    }

    [Fact]
    public async Task GivenPostsWithDifferentViews_WhenFilteringPopular_ThenMostViewedComesFirst()
    {
        await GivenPosts(("low", 200), ("high", 900), ("middle", 500));

        var titles = await BlogQueries.PopularTitles(database, 100, CancellationToken.None);

        Assert.Equal(["high", "middle", "low"], titles);
    }
}
'''

totals_good = LIFETIME.format(cls="TotalViewsPerBlogTests") + '''
    async Task GivenBlog(string name, params int[] views)
    {
        database.Blogs.Add(new Blog { Name = name, Posts = [.. views.Select((count, index) => new Post { Title = $"post {index}", Views = count })] });
        await database.SaveChangesAsync();
    }

    [Fact]
    public async Task GivenBlogsWithSeveralPosts_WhenTotalingViews_ThenEachBlogHasItsOwnSumInNameOrder()
    {
        await GivenBlog("b", 3, 4, 5);
        await GivenBlog("a", 10);

        var totals = await BlogQueries.TotalViewsPerBlog(database, CancellationToken.None);

        Assert.Equal([new BlogTotal("a", 10), new BlogTotal("b", 12)], totals);
    }

    [Fact]
    public async Task GivenBlogWithoutPosts_WhenTotalingViews_ThenItsTotalIsZero()
    {
        await GivenBlog("empty");

        var totals = await BlogQueries.TotalViewsPerBlog(database, CancellationToken.None);

        Assert.Equal([new BlogTotal("empty", 0)], totals);
    }
}
'''

def variants(good, cls, helper, first_test, calls_marker):
    v = {}
    v["failing-assertion"] = good.replace("Assert.Equal(", "Assert.NotEqual(", 1)
    import re
    v["helper-async-suffix"] = re.sub(rf"\b{helper}\b", helper + "Async", good)
    v["helper-not-gherkin"] = re.sub(rf"\b{helper}\b", "Seed" + helper[len("Given"):], good)
    names = re.findall(r"public async Task (Given\w+_When\w+_Then\w+)\(", good)
    plain = good
    for i, n in enumerate(names):
        plain = plain.replace(n, f"Scenario{i}")
    v["plain-test-names"] = plain
    return v

popular_bad = variants(popular_good, "PopularTitlesTests", "GivenPosts", None, None)
popular_bad["weak-single-post"] = LIFETIME.format(cls="PopularTitlesTests") + '''
    [Fact]
    public async Task GivenOnePopularPost_WhenFilteringPopular_ThenItIsReturned()
    {
        database.Blogs.Add(new Blog { Name = "blog", Posts = [new Post { Title = "big", Views = 500 }] });
        await database.SaveChangesAsync();

        var titles = await BlogQueries.PopularTitles(database, 100, CancellationToken.None);

        Assert.Equal(["big"], titles);
    }
}
'''
popular_bad["connection-in-test"] = '''public class PopularTitlesTests
{
    [Fact]
    public async Task GivenPostsAroundTheThreshold_WhenFilteringPopular_ThenOnlyPostsAboveItAreReturned()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var database = new BlogContext(new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        database.Blogs.Add(new Blog { Name = "blog", Posts = [new Post { Title = "below", Views = 99 }, new Post { Title = "exact", Views = 100 }, new Post { Title = "above", Views = 101 }] });
        await database.SaveChangesAsync();

        var titles = await BlogQueries.PopularTitles(database, 100, CancellationToken.None);

        Assert.Equal(["above"], titles);
        await database.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task GivenPostsWithDifferentViews_WhenFilteringPopular_ThenMostViewedComesFirst()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var database = new BlogContext(new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        database.Blogs.Add(new Blog { Name = "blog", Posts = [new Post { Title = "low", Views = 200 }, new Post { Title = "high", Views = 900 }, new Post { Title = "middle", Views = 500 }] });
        await database.SaveChangesAsync();

        var titles = await BlogQueries.PopularTitles(database, 100, CancellationToken.None);

        Assert.Equal(["high", "middle", "low"], titles);
        await database.DisposeAsync();
        await connection.DisposeAsync();
    }
}
'''
popular_bad["discard-usings"] = '''public class PopularTitlesTests
{
    static async Task<(SqliteConnection, BlogContext)> GivenDatabase(params (string Title, int Views)[] posts)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var database = new BlogContext(new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        database.Blogs.Add(new Blog { Name = "blog", Posts = [.. posts.Select(post => new Post { Title = post.Title, Views = post.Views })] });
        await database.SaveChangesAsync();
        return (connection, database);
    }

    [Fact]
    public async Task GivenPostsAroundTheThreshold_WhenFilteringPopular_ThenOnlyPostsAboveItAreReturned()
    {
        var (connection, database) = await GivenDatabase(("below", 99), ("exact", 100), ("above", 101));
        using var _ = connection;
        using var __ = database;

        var titles = await BlogQueries.PopularTitles(database, 100, CancellationToken.None);

        Assert.Equal(["above"], titles);
    }

    [Fact]
    public async Task GivenPostsWithDifferentViews_WhenFilteringPopular_ThenMostViewedComesFirst()
    {
        var (connection, database) = await GivenDatabase(("low", 200), ("high", 900), ("middle", 500));
        using var _ = connection;
        using var __ = database;

        var titles = await BlogQueries.PopularTitles(database, 100, CancellationToken.None);

        Assert.Equal(["high", "middle", "low"], titles);
    }
}
'''

totals_bad = variants(totals_good, "TotalViewsPerBlogTests", "GivenBlog", None, None)
totals_bad["weak-single-post"] = LIFETIME.format(cls="TotalViewsPerBlogTests") + '''
    [Fact]
    public async Task GivenOneBlogWithOnePost_WhenTotalingViews_ThenTheTotalIsReturned()
    {
        database.Blogs.Add(new Blog { Name = "only", Posts = [new Post { Title = "p", Views = 1 }] });
        await database.SaveChangesAsync();

        var totals = await BlogQueries.TotalViewsPerBlog(database, CancellationToken.None);

        Assert.Equal([new BlogTotal("only", 1)], totals);
    }
}
'''
totals_bad["connection-in-test"] = '''public class TotalViewsPerBlogTests
{
    [Fact]
    public async Task GivenBlogsWithSeveralPosts_WhenTotalingViews_ThenEachBlogHasItsOwnSumInNameOrder()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var database = new BlogContext(new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        database.Blogs.Add(new Blog { Name = "b", Posts = [new Post { Title = "x", Views = 3 }, new Post { Title = "y", Views = 4 }, new Post { Title = "z", Views = 5 }] });
        database.Blogs.Add(new Blog { Name = "a", Posts = [new Post { Title = "w", Views = 10 }] });
        database.Blogs.Add(new Blog { Name = "empty" });
        await database.SaveChangesAsync();

        var totals = await BlogQueries.TotalViewsPerBlog(database, CancellationToken.None);

        Assert.Equal([new BlogTotal("a", 10), new BlogTotal("b", 12), new BlogTotal("empty", 0)], totals);
        await database.DisposeAsync();
        await connection.DisposeAsync();
    }
}
'''
totals_bad["discard-usings"] = totals_bad["connection-in-test"].replace(
    "        var connection = new SqliteConnection", "        var connection = new SqliteConnection", 1)
totals_bad["discard-usings"] = '''public class TotalViewsPerBlogTests
{
    static async Task<(SqliteConnection, BlogContext)> GivenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var database = new BlogContext(new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        database.Blogs.Add(new Blog { Name = "b", Posts = [new Post { Title = "x", Views = 3 }, new Post { Title = "y", Views = 4 }, new Post { Title = "z", Views = 5 }] });
        database.Blogs.Add(new Blog { Name = "a", Posts = [new Post { Title = "w", Views = 10 }] });
        database.Blogs.Add(new Blog { Name = "empty" });
        await database.SaveChangesAsync();
        return (connection, database);
    }

    [Fact]
    public async Task GivenBlogsWithSeveralPosts_WhenTotalingViews_ThenEachBlogHasItsOwnSumInNameOrder()
    {
        var (connection, database) = await GivenDatabase();
        using var _ = connection;
        using var __ = database;

        var totals = await BlogQueries.TotalViewsPerBlog(database, CancellationToken.None);

        Assert.Equal([new BlogTotal("a", 10), new BlogTotal("b", 12), new BlogTotal("empty", 0)], totals);
    }
}
'''

refs = {
    "popular-titles": {"good": popular_good, "bad": popular_bad},
    "total-views-per-blog": {"good": totals_good, "bad": totals_bad},
}
json.dump(cases, open("unit-testing/evals/cases.json", "w"), indent=2)
open("unit-testing/evals/cases.json", "a").write("\n")
json.dump(refs, open("unit-testing/evals/references.json", "w"), indent=2)
open("unit-testing/evals/references.json", "a").write("\n")
