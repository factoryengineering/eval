import json
import re

ALL_RULES = ["given-when-then", "gherkin-helpers", "no-discard-usings", "no-connection-in-tests"]
SUBJECT = "unit-testing/evals/subjects/BlogQueries.cs"

MODEL = ("The entities are: Blog { int Id; required string Name; List<Post> Posts; List<Contributor> Contributors }, "
         "Post { int Id; required string Title; int Views; int BlogId; Blog Blog }, Contributor { int Id; required string Name; int BlogId; Blog Blog }. "
         "BlogContext has a public constructor taking DbContextOptions<BlogContext> and exposes DbSet properties Blogs, Posts and Contributors. ")
COMMON = (MODEL + "BlogContext, Blog, Post and Contributor already exist in the imported namespace EfFixtures, and so do BlogQueries and BlogTotal, "
          "so do not redefine them; define any other types or helpers you need. The only packages available are xUnit and EF Core with its SQLite provider "
          "(Microsoft.Data.Sqlite, UseSqlite); the InMemory provider is not available. Return only a C# code block.\n\nThe code under test:\n\n{{subject}}")

MUTANTS = {
    "popular-titles": [
        {"name": "boundary", "find": "post.Views > minViews", "replace": "post.Views >= minViews"},
        {"name": "no-filter", "find": "post.Views > minViews", "replace": "true"},
        {"name": "ascending", "find": "orderby post.Views descending", "replace": "orderby post.Views"},
    ],
    "total-views-per-blog": [
        {"name": "count-not-sum", "find": "blog.Posts.Sum(post => post.Views)", "replace": "blog.Posts.Count()"},
        {"name": "descending-order", "find": "orderby blog.Name", "replace": "orderby blog.Name descending"},
        {"name": "first-post-only", "find": "blog.Posts.Sum(post => post.Views)", "replace": "blog.Posts.OrderBy(post => post.Id).Select(post => post.Views).FirstOrDefault()"},
    ],
    "total-post-views": [
        {"name": "count-not-sum", "find": "database.Posts.SumAsync(post => post.Views, cancellationToken)", "replace": "database.Posts.CountAsync(cancellationToken)"},
        {"name": "off-by-one", "find": "post => post.Views, cancellationToken", "replace": "post => post.Views + 1, cancellationToken"},
        {"name": "max-not-sum", "find": "database.Posts.SumAsync(post => post.Views, cancellationToken)", "replace": "database.Posts.MaxAsync(post => post.Views, cancellationToken)"},
    ],
}

METHODS = {"popular-titles": "PopularTitles", "total-views-per-blog": "TotalViewsPerBlog", "total-post-views": "TotalPostViews"}

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

INLINE = '''        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var database = new BlogContext(new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
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

allviews_good = LIFETIME.format(cls="TotalPostViewsTests") + '''
    async Task GivenPostViews(params int[] views)
    {
        database.Blogs.Add(new Blog { Name = "blog", Posts = [.. views.Select((count, index) => new Post { Title = $"post {index}", Views = count })] });
        await database.SaveChangesAsync();
    }

    [Fact]
    public async Task GivenSeveralPosts_WhenTotalingAllViews_ThenTheSumOfTheirViewsIsReturned()
    {
        await GivenPostViews(3, 4, 10);

        var total = await BlogQueries.TotalPostViews(database, CancellationToken.None);

        Assert.Equal(17, total);
    }

    [Fact]
    public async Task GivenNoPosts_WhenTotalingAllViews_ThenTheTotalIsZero()
    {
        var total = await BlogQueries.TotalPostViews(database, CancellationToken.None);

        Assert.Equal(0, total);
    }
}
'''

GOODS = {"popular-titles": (popular_good, "GivenPosts"), "total-views-per-blog": (totals_good, "GivenBlog"), "total-post-views": (allviews_good, "GivenPostViews")}


def helper_variants(good, helper):
    v = {}
    v["helper-async-suffix"] = re.sub(rf"\b{helper}\b", helper + "Async", good)
    v["helper-not-gherkin"] = re.sub(rf"\b{helper}\b", "Seed" + helper[len("Given"):], good)
    plain = good
    for i, n in enumerate(re.findall(r"public async Task (Given\w+_When\w+_Then\w+)\(", good)):
        plain = plain.replace(n, f"Scenario{i}")
    v["plain-test-names"] = plain
    v["failing-assertion"] = good.replace("Assert.Equal(", "Assert.NotEqual(", 1)
    return v


def weak_single(subject):
    cls = {"popular-titles": "PopularTitlesTests", "total-views-per-blog": "TotalViewsPerBlogTests", "total-post-views": "TotalPostViewsTests"}[subject]
    tests = {
        "popular-titles": '''    [Fact]
    public async Task GivenOnePopularPost_WhenFilteringPopular_ThenItIsReturned()
    {
        database.Blogs.Add(new Blog { Name = "blog", Posts = [new Post { Title = "big", Views = 500 }] });
        await database.SaveChangesAsync();

        var titles = await BlogQueries.PopularTitles(database, 100, CancellationToken.None);

        Assert.Equal(["big"], titles);
    }
''',
        "total-views-per-blog": '''    [Fact]
    public async Task GivenOneBlogWithOnePost_WhenTotalingViews_ThenTheTotalIsReturned()
    {
        database.Blogs.Add(new Blog { Name = "only", Posts = [new Post { Title = "p", Views = 1 }] });
        await database.SaveChangesAsync();

        var totals = await BlogQueries.TotalViewsPerBlog(database, CancellationToken.None);

        Assert.Equal([new BlogTotal("only", 1)], totals);
    }
''',
        "total-post-views": '''    [Fact]
    public async Task GivenOnePost_WhenTotalingAllViews_ThenItsViewsAreReturned()
    {
        database.Blogs.Add(new Blog { Name = "blog", Posts = [new Post { Title = "p", Views = 1 }] });
        await database.SaveChangesAsync();

        var total = await BlogQueries.TotalPostViews(database, CancellationToken.None);

        Assert.Equal(1, total);
    }
''',
    }[subject]
    return LIFETIME.format(cls=cls) + "\n" + tests + "}\n"


def connection_in_test(subject):
    cls = {"popular-titles": "PopularTitlesTests", "total-views-per-blog": "TotalViewsPerBlogTests", "total-post-views": "TotalPostViewsTests"}[subject]
    seed, call, expect = {
        "popular-titles": (
            'database.Blogs.Add(new Blog { Name = "blog", Posts = [new Post { Title = "below", Views = 99 }, new Post { Title = "exact", Views = 100 }, new Post { Title = "above", Views = 101 }, new Post { Title = "top", Views = 900 }] });',
            "BlogQueries.PopularTitles(database, 100, CancellationToken.None)", 'Assert.Equal(["top", "above"], result);'),
        "total-views-per-blog": (
            'database.Blogs.Add(new Blog { Name = "b", Posts = [new Post { Title = "x", Views = 3 }, new Post { Title = "y", Views = 4 }] }); database.Blogs.Add(new Blog { Name = "a", Posts = [new Post { Title = "w", Views = 10 }] });',
            "BlogQueries.TotalViewsPerBlog(database, CancellationToken.None)", 'Assert.Equal([new BlogTotal("a", 10), new BlogTotal("b", 7)], result);'),
        "total-post-views": (
            'database.Blogs.Add(new Blog { Name = "blog", Posts = [new Post { Title = "x", Views = 3 }, new Post { Title = "y", Views = 14 }] });',
            "BlogQueries.TotalPostViews(database, CancellationToken.None)", "Assert.Equal(17, result);"),
    }[subject]
    return f'''public class {cls}
{{
    [Fact]
    public async Task GivenSeededPosts_WhenQuerying_ThenTheExpectedResultIsReturned()
    {{
{INLINE}        {seed}
        await database.SaveChangesAsync();

        var result = await {call};

        {expect}
        await database.DisposeAsync();
        await connection.DisposeAsync();
    }}
}}
'''


def orphan_posts(subject):
    good, helper = GOODS[subject]
    if subject == "popular-titles":
        return good.replace('database.Blogs.Add(new Blog { Name = "blog", Posts = [.. posts.Select(post => new Post { Title = post.Title, Views = post.Views })] });',
                            'database.Posts.AddRange(posts.Select(post => new Post { Title = post.Title, Views = post.Views }));')
    return good.replace('database.Blogs.Add(new Blog { Name = "blog", Posts = [.. views.Select((count, index) => new Post { Title = $"post {index}", Views = count })] });',
                        'database.Posts.AddRange(views.Select((count, index) => new Post { Title = $"post {index}", Views = count }));')


LINES = {
    "given-when-then": {"prefix": "naming", "rules": ["given-when-then"], "graded": ["rule given-when-then"], "bad": ["plain-test-names"],
                        "subjects": ["popular-titles", "total-views-per-blog"]},
    "gherkin-helpers": {"prefix": "helpers", "rules": ["gherkin-helpers"], "graded": ["rule gherkin-helpers"], "bad": ["helper-async-suffix", "helper-not-gherkin"],
                        "subjects": ["popular-titles", "total-views-per-blog"]},
    "setup-lifetime": {"prefix": "lifetime", "rules": ["no-connection-in-tests"], "graded": ["rule no-connection-in-tests"], "bad": ["connection-in-test"],
                       "subjects": ["popular-titles", "total-views-per-blog"]},
    "seed-parents": {"prefix": "seed", "rules": [], "graded": ["test failed"], "bad": ["orphan-posts", "failing-assertion"],
                     "subjects": ["popular-titles", "total-post-views"]},
}


def make_case(case_id, subject, targets, rules, graded=None, guard=False):
    tests = {"subject": SUBJECT, "minTests": 1, "rules": rules, "mutants": MUTANTS[subject]}
    if graded:
        tests["graded"] = graded
    c = {"id": case_id, "prompt": f"Write xUnit tests for `BlogQueries.{METHODS[subject]}`. " + COMMON, "targets": targets, "tests": tests, "must": []}
    if guard:
        c["guard"] = True
    return c


def bad_variant(name, subject):
    good, helper = GOODS[subject]
    if name == "connection-in-test":
        return connection_in_test(subject)
    if name == "orphan-posts":
        return orphan_posts(subject)
    if name == "weak-single-post":
        return weak_single(subject)
    return helper_variants(good, helper)[name]


cases, refs = [], {}
for line, spec in LINES.items():
    for subject in spec["subjects"]:
        case_id = f"{spec['prefix']}-{subject}"
        cases.append(make_case(case_id, subject, [line], spec["rules"], spec["graded"]))
        refs[case_id] = {"good": GOODS[subject][0], "bad": {name: bad_variant(name, subject) for name in spec["bad"]}}
for subject in METHODS:
    case_id = f"guard-{subject}"
    cases.append(make_case(case_id, subject, [], ALL_RULES, guard=True))
    names = ["plain-test-names", "helper-async-suffix", "helper-not-gherkin", "connection-in-test", "weak-single-post", "failing-assertion"]
    if subject != "total-views-per-blog":
        names.append("orphan-posts")
    refs[case_id] = {"good": GOODS[subject][0], "bad": {name: bad_variant(name, subject) for name in names}}

with open("unit-testing/evals/cases.json", "w") as f:
    json.dump(cases, f, indent=2)
    f.write("\n")
with open("unit-testing/evals/references.json", "w") as f:
    json.dump(refs, f, indent=2)
    f.write("\n")
