using EfFixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public class BlogServiceTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private DbContextOptions<BlogContext> options = null!;
    private BlogContext database = null!;
    private BlogService service = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options;
        using var schema = new BlogContext(options);
        await schema.Database.EnsureCreatedAsync();
        database = new BlogContext(options);
        service = new BlogService(database);
    }

    public async Task DisposeAsync()
    {
        await database.DisposeAsync();
        await connection.DisposeAsync();
    }

    private async Task<int> GivenBlogSaved(string name)
    {
        using var seeding = new BlogContext(options);
        var blog = new Blog { Name = name };
        seeding.Blogs.Add(blog);
        await seeding.SaveChangesAsync();
        return blog.Id;
    }

    private async Task<(int BlogId, int PostId)> GivenBlogWithPostSaved(string blogName, string postTitle)
    {
        using var seeding = new BlogContext(options);
        var blog = new Blog { Name = blogName, Posts = [new Post { Title = postTitle }] };
        seeding.Blogs.Add(blog);
        await seeding.SaveChangesAsync();
        return (blog.Id, blog.Posts[0].Id);
    }

    private async Task<List<string>> ThenStoredBlogNames()
    {
        using var verification = new BlogContext(options);
        var namesQuery =
            from blog in verification.Blogs
            orderby blog.Name
            select blog.Name;
        var storedNames = await namesQuery.ToListAsync();
        return storedNames;
    }

    private async Task<List<(string Title, int BlogId, int Views)>> ThenStoredPosts()
    {
        using var verification = new BlogContext(options);
        var postsQuery =
            from post in verification.Posts
            orderby post.Title
            select new { post.Title, post.BlogId, post.Views };
        var storedPosts = await postsQuery.ToListAsync();
        return [.. storedPosts.Select(post => (post.Title, post.BlogId, post.Views))];
    }

    [Fact]
    public async Task GivenNoBlogs_WhenAddingBlog_ThenBlogIsPersistedWithGeneratedId()
    {
        var cancellationToken = CancellationToken.None;

        var added = await service.AddBlogAsync("first", cancellationToken);

        Assert.True(added.Id > 0);
        Assert.Equal(["first"], await ThenStoredBlogNames());
    }

    [Fact]
    public async Task GivenExistingBlog_WhenAddingAnotherBlog_ThenBothArePersisted()
    {
        var cancellationToken = CancellationToken.None;
        await GivenBlogSaved("first");

        await service.AddBlogAsync("second", cancellationToken);

        Assert.Equal(["first", "second"], await ThenStoredBlogNames());
    }

    [Fact]
    public async Task GivenExistingBlog_WhenAddingPost_ThenPostIsPersistedUnderThatBlog()
    {
        var cancellationToken = CancellationToken.None;
        var blogId = await GivenBlogSaved("first");

        var added = await service.AddPostAsync(blogId, "hello", cancellationToken);

        Assert.True(added.Id > 0);
        Assert.Equal(blogId, added.BlogId);
        Assert.Equal([("hello", blogId, 0)], await ThenStoredPosts());
    }

    [Fact]
    public async Task GivenTwoBlogs_WhenAddingPostToOne_ThenOtherBlogHasNoPosts()
    {
        var cancellationToken = CancellationToken.None;
        var firstId = await GivenBlogSaved("first");
        await GivenBlogSaved("second");

        await service.AddPostAsync(firstId, "hello", cancellationToken);

        var storedPosts = await ThenStoredPosts();
        Assert.Single(storedPosts);
        Assert.Equal(firstId, storedPosts[0].BlogId);
    }

    [Fact]
    public async Task GivenMissingBlog_WhenAddingPost_ThenBlogNotFoundIsThrownAndNothingIsPersisted()
    {
        var cancellationToken = CancellationToken.None;
        var blogId = await GivenBlogSaved("first");
        var missingId = blogId + 100;

        var failure = await Assert.ThrowsAsync<BlogNotFoundException>(
            () => service.AddPostAsync(missingId, "orphan", cancellationToken));

        Assert.Equal(missingId, failure.BlogId);
        Assert.Empty(await ThenStoredPosts());
    }

    [Fact]
    public async Task GivenTwoBlogs_WhenRenamingOne_ThenOnlyThatBlogChanges()
    {
        var cancellationToken = CancellationToken.None;
        var firstId = await GivenBlogSaved("first");
        await GivenBlogSaved("second");

        await service.RenameBlogAsync(firstId, "renamed", cancellationToken);

        Assert.Equal(["renamed", "second"], await ThenStoredBlogNames());
    }

    [Fact]
    public async Task GivenMissingBlog_WhenRenaming_ThenBlogNotFoundIsThrownAndNamesAreUnchanged()
    {
        var cancellationToken = CancellationToken.None;
        var blogId = await GivenBlogSaved("first");
        var missingId = blogId + 100;

        var failure = await Assert.ThrowsAsync<BlogNotFoundException>(
            () => service.RenameBlogAsync(missingId, "renamed", cancellationToken));

        Assert.Equal(missingId, failure.BlogId);
        Assert.Equal(["first"], await ThenStoredBlogNames());
    }

    [Fact]
    public async Task GivenExistingPost_WhenRetitling_ThenTitleChangesAndBlogAndViewsAreKept()
    {
        var cancellationToken = CancellationToken.None;
        var (blogId, postId) = await GivenBlogWithPostSaved("first", "old");

        await service.RetitlePostAsync(postId, "new", cancellationToken);

        Assert.Equal([("new", blogId, 0)], await ThenStoredPosts());
    }

    [Fact]
    public async Task GivenMissingPost_WhenRetitling_ThenPostNotFoundIsThrownAndTitlesAreUnchanged()
    {
        var cancellationToken = CancellationToken.None;
        var (blogId, postId) = await GivenBlogWithPostSaved("first", "old");
        var missingId = postId + 100;

        var failure = await Assert.ThrowsAsync<PostNotFoundException>(
            () => service.RetitlePostAsync(missingId, "new", cancellationToken));

        Assert.Equal(missingId, failure.PostId);
        Assert.Equal([("old", blogId, 0)], await ThenStoredPosts());
    }
}
