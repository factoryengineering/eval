using EfFixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public class LinqTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private DbContextOptions<BlogContext> options = null!;
    private BlogContext database = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options;
        using var schema = new BlogContext(options);
        await schema.Database.EnsureCreatedAsync();
        database = new BlogContext(options);
    }

    public async Task DisposeAsync()
    {
        await database.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static Blog GivenBlogWithPosts(string name, params (string Title, int Views)[] posts) =>
        new() { Name = name, Posts = [.. posts.Select(post => new Post { Title = post.Title, Views = post.Views })] };

    private static List<Blog> GivenTwoBlogs() =>
    [
        GivenBlogWithPosts("b", ("p", 3), ("q", 4)),
        GivenBlogWithPosts("c", ("r", 10)),
    ];

    private static List<Blog> GivenApplesAndBananas() =>
        [GivenBlogWithPosts("b", ("apple", 1), ("banana", 2))];

    private async Task GivenBlogsSaved(IEnumerable<Blog> blogs)
    {
        using var seeding = new BlogContext(options);
        seeding.Blogs.AddRange(blogs);
        await seeding.SaveChangesAsync();
    }

    [Fact]
    public async Task GivenBlogWithTwoPosts_WhenTotalingViews_ThenTotalIsComputedInTheDatabase()
    {
        var cancellationToken = CancellationToken.None;
        await GivenBlogsSaved([GivenBlogWithPosts("b", ("p", 3), ("q", 4))]);

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
        await GivenBlogsSaved(GivenTwoBlogs());

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
        await GivenBlogsSaved(GivenApplesAndBananas());

        var titlesQuery =
            from post in database.Posts
            where post.Title.StartsWith("app")
            select post.Title;

        Assert.Contains("WHERE", titlesQuery.ToQueryString());

        var matchingTitles = await titlesQuery.ToListAsync(cancellationToken);
        Assert.Equal(["apple"], matchingTitles);
    }

    [Fact]
    public async Task GivenSeededPosts_WhenProjectingValues_ThenNothingIsTracked()
    {
        var cancellationToken = CancellationToken.None;
        await GivenBlogsSaved(GivenApplesAndBananas());

        var titlesQuery =
            from post in database.Posts
            orderby post.Title
            select post.Title;

        var titles = await titlesQuery.ToListAsync(cancellationToken);

        Assert.Equal(["apple", "banana"], titles);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GivenSeededPosts_WhenLoadingEntities_ThenTheyAreTracked()
    {
        var cancellationToken = CancellationToken.None;
        await GivenBlogsSaved(GivenApplesAndBananas());

        var postsQuery =
            from post in database.Posts
            select post;

        var loadedPosts = await postsQuery.ToListAsync(cancellationToken);

        Assert.Equal(2, loadedPosts.Count);
        Assert.Equal(2, database.ChangeTracker.Entries<Post>().Count());
    }
}
