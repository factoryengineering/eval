using EvalRunner;

public class SampleRunnerTests
{
    static Task<Result> Run(string code) => SampleRunner.Run(new Request("t", code));

    [Fact]
    public async Task ReportsCompileErrors()
    {
        var r = await Run("public int Bad() { return \"x\"; }");
        Assert.False(r.Compiled);
        Assert.Contains(r.Diagnostics, d => d.StartsWith("CS0029"));
    }

    [Fact]
    public async Task CapturesSqlOfAsyncMethodWithLooseMembers()
    {
        var r = await Run("""
            public async Task<List<string>> Titles(BlogContext db, CancellationToken ct)
            {
                var query = from p in db.Posts where p.Views > 100 orderby p.Views descending select p.Title;
                return await query.ToListAsync(ct);
            }
            """);
        Assert.True(r.Compiled);
        Assert.All(r.Invoked, i => Assert.Null(i.Error));
        var sql = Assert.Single(r.Sql);
        Assert.Contains("WHERE", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Equal(1, r.Syntax!.QueryExpressions);
    }

    [Fact]
    public async Task UntranslatableHelperFailsAtRuntime()
    {
        var r = await Run("""
            public async Task<List<Post>> Find(BlogContext db, string prefix) =>
                await db.Posts.Where(p => TitleRules.HasPrefix(p.Title, prefix)).ToListAsync();
            """);
        Assert.True(r.Compiled);
        Assert.Contains(r.Invoked, i => i.Error is not null && i.Error.StartsWith("InvalidOperationException"));
    }

    [Fact]
    public async Task ClientSideFilterLeavesNoWhereInSql()
    {
        var r = await Run("""
            public async Task<List<Post>> Find(BlogContext db, string prefix)
            {
                var all = await db.Posts.ToListAsync();
                return all.Where(p => TitleRules.HasPrefix(p.Title, prefix)).ToList();
            }
            """);
        Assert.All(r.Invoked, i => Assert.Null(i.Error));
        Assert.DoesNotContain(r.Sql, s => s.Contains("WHERE"));
    }

    [Fact]
    public async Task SplitQueryIssuesOneCommandPerCollection()
    {
        const string body = """
            public async Task<List<Blog>> Load(BlogContext db) =>
                await db.Blogs.Include(b => b.Posts).Include(b => b.Contributors){0}.ToListAsync();
            """;
        var split = await Run(string.Format(body, ".AsSplitQuery()"));
        var single = await Run(string.Format(body, ""));
        Assert.Equal(3, split.Sql.Count);
        Assert.Single(single.Sql);
    }

    [Fact]
    public async Task CapturesWritesAndClassWithContextConstructor()
    {
        var r = await Run("""
            public class PostService(BlogContext db)
            {
                public async Task Rename(int id, string title)
                {
                    var post = await db.Posts.SingleAsync(p => p.Id == id);
                    post.Title = title;
                    await db.SaveChangesAsync();
                }
            }
            """);
        Assert.All(r.Invoked, i => Assert.Null(i.Error));
        Assert.Contains(r.Sql, s => s.Contains("UPDATE \"Posts\""));
    }

    [Fact]
    public async Task CapturesToQueryStringOfReturnedQuery()
    {
        var r = await Run("public IQueryable<Post> Popular(BlogContext db) => db.Posts.Where(p => p.Views > 100);");
        Assert.Contains("WHERE", Assert.Single(r.QueryStrings));
        Assert.Single(r.Sql);
    }

    [Fact]
    public async Task RejectsForbiddenApisWithoutRunningThem()
    {
        var r = await Run("public string Read() => System.IO.File.ReadAllText(\"/etc/hosts\");");
        Assert.Contains(r.Forbidden, f => f.StartsWith("System.IO"));
        Assert.Empty(r.Invoked);
    }

    [Fact]
    public async Task CountsOnlyMethodSyntaxOperatorsAppliedToQueryables()
    {
        var r = await Run("""
            public async Task<List<string>> Titles(BlogContext db)
            {
                var query = db.Posts.Where(p => p.Views > 1).OrderBy(p => p.Views);
                var posts = await query.ToListAsync();
                return posts.Select(p => p.Title).ToList();
            }
            """);
        Assert.Equal(["OrderBy", "Where"], r.Syntax!.QueryableOperators.Order());
        var keyword = await Run("""
            public async Task<List<string>> Titles(BlogContext db)
            {
                var query = from p in db.Posts where p.Views > 1 orderby p.Views select p.Title;
                var titles = await query.ToListAsync();
                return titles.Select(t => t.ToUpper()).ToList();
            }
            """);
        Assert.Empty(keyword.Syntax!.QueryableOperators);
    }

    [Fact]
    public async Task DistinguishesAwaitedQueryVariablesFromInlineAwaitedQueries()
    {
        var separate = await Run("""
            public async Task<List<string>> Titles(BlogContext db, CancellationToken ct)
            {
                var query = from p in db.Posts select p.Title;
                var titles = await query
                    .ToListAsync(ct);
                return titles;
            }
            """);
        Assert.Equal(1, separate.Syntax!.AwaitedQueryLocals);
        Assert.Equal(0, separate.Syntax.InlineAwaitedQueries);

        var inlineParen = await Run("public async Task<List<string>> Titles(BlogContext db) => await (from p in db.Posts select p.Title).ToListAsync();");
        Assert.Equal(0, inlineParen.Syntax!.AwaitedQueryLocals);
        Assert.Equal(1, inlineParen.Syntax.InlineAwaitedQueries);

        var chained = await Run("public async Task<List<Post>> Posts(BlogContext db) => await db.Posts.Where(p => p.Views > 1).ToListAsync();");
        Assert.Equal(1, chained.Syntax!.InlineAwaitedQueries);

        var notAQuery = await Run("public async Task Save(BlogContext db) { var saving = db.SaveChangesAsync(); await saving; await db.SaveChangesAsync(); }");
        Assert.Equal(0, notAQuery.Syntax!.AwaitedQueryLocals + notAQuery.Syntax.InlineAwaitedQueries);
    }

    [Fact]
    public async Task ExtractsDeclaredIdentifiers()
    {
        var r = await Run("""
            public int Count(BlogContext db) => (from post in db.Posts select post).Select(p => p.Views).Count(v => v > 1);
            """);
        Assert.Contains("post", r.Syntax!.Identifiers);
        Assert.Contains("p", r.Syntax.Identifiers);
        Assert.Contains("v", r.Syntax.Identifiers);
        Assert.Contains("db", r.Syntax.Identifiers);
    }
}
