using EfFixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public class LinqTests
{
    private static Blog GivenBlogWithPosts(string name, params (string Title, int Views)[] posts) =>
        new() { Name = name, Posts = [.. posts.Select(post => new Post { Title = post.Title, Views = post.Views })] };

    private static List<Blog> GivenTwoBlogs() =>
    [
        GivenBlogWithPosts("b", ("p", 3), ("q", 4)),
        GivenBlogWithPosts("c", ("r", 10)),
    ];

    private static List<Blog> GivenApplesAndBananas() =>
        [GivenBlogWithPosts("b", ("apple", 1), ("banana", 2))];

    private static async Task<(SqliteConnection Connection, BlogContext Database)> GivenSeededContext(
        IEnumerable<Blog> blogs, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options;
        var database = new BlogContext(options);
        await database.Database.EnsureCreatedAsync(cancellationToken);
        database.Blogs.AddRange(blogs);
        await database.SaveChangesAsync(cancellationToken);
        return (connection, database);
    }

    [Fact]
    public async Task GivenBlogWithTwoPosts_WhenTotalingViews_ThenTotalIsComputedInTheDatabase()
    {
        var cancellationToken = CancellationToken.None;
        var (connection, database) = await GivenSeededContext(
            [GivenBlogWithPosts("b", ("p", 3), ("q", 4))], cancellationToken);
        using var _ = connection;
        using var __ = database;

        var totalsQuery =
            from post in database.Posts
            group post by post.BlogId into blogPosts
            select new { BlogId = blogPosts.Key, TotalViews = blogPosts.Sum(item => item.Views) };

        Assert.Contains("GROUP BY", totalsQuery.ToQueryString());

        var blogTotal = await totalsQuery.SingleAsync(cancellationToken);
        Assert.Equal(7, blogTotal.TotalViews);
    }

    [Fact]
    public async Task GivenTwoBlogs_WhenTotalingViews_ThenEachBlogHasItsOwnTotal()
    {
        var cancellationToken = CancellationToken.None;
        var (connection, database) = await GivenSeededContext(GivenTwoBlogs(), cancellationToken);
        using var _ = connection;
        using var __ = database;

        var totalsQuery =
            from post in database.Posts
            group post by post.BlogId into blogPosts
            orderby blogPosts.Key
            select new { BlogId = blogPosts.Key, TotalViews = blogPosts.Sum(item => item.Views) };

        var blogTotals = await totalsQuery.ToListAsync(cancellationToken);
        Assert.Equal([7, 10], blogTotals.Select(blogTotal => blogTotal.TotalViews));
    }

    [Fact]
    public async Task GivenApplesAndBananas_WhenFilteringByPrefix_ThenOnlyMatchingTitlesAreReturned()
    {
        var cancellationToken = CancellationToken.None;
        var (connection, database) = await GivenSeededContext(GivenApplesAndBananas(), cancellationToken);
        using var _ = connection;
        using var __ = database;

        var titlesQuery =
            from post in database.Posts
            where post.Title.StartsWith("app")
            select post.Title;

        Assert.Contains("WHERE", titlesQuery.ToQueryString());

        var matchingTitles = await titlesQuery.ToListAsync(cancellationToken);
        Assert.Equal(["apple"], matchingTitles);
    }
}
