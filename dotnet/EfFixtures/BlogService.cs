using Microsoft.EntityFrameworkCore;

namespace EfFixtures;

public class BlogNotFoundException(int blogId) : Exception($"Blog {blogId} does not exist.")
{
    public int BlogId { get; } = blogId;
}

public class PostNotFoundException(int postId) : Exception($"Post {postId} does not exist.")
{
    public int PostId { get; } = postId;
}

public class BlogService(BlogContext database)
{
    public async Task RenameBlogAsync(int blogId, string name, CancellationToken cancellationToken = default)
    {
        var blogQuery =
            from blog in database.Blogs
            where blog.Id == blogId
            select blog;
        var existingBlog = await blogQuery.SingleOrDefaultAsync(cancellationToken)
            ?? throw new BlogNotFoundException(blogId);

        existingBlog.Name = name;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task RetitlePostAsync(int postId, string title, CancellationToken cancellationToken = default)
    {
        var postQuery =
            from post in database.Posts
            where post.Id == postId
            select post;
        var existingPost = await postQuery.SingleOrDefaultAsync(cancellationToken)
            ?? throw new PostNotFoundException(postId);

        existingPost.Title = title;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<Blog> AddBlogAsync(string name, CancellationToken cancellationToken = default)
    {
        var blog = new Blog { Name = name };
        database.Blogs.Add(blog);
        await database.SaveChangesAsync(cancellationToken);
        return blog;
    }

    public async Task<Post> AddPostAsync(int blogId, string title, CancellationToken cancellationToken = default)
    {
        var blogExistsQuery =
            from blog in database.Blogs
            where blog.Id == blogId
            select blog.Id;
        var blogExists = await blogExistsQuery.AnyAsync(cancellationToken);
        if (!blogExists)
            throw new BlogNotFoundException(blogId);

        var post = new Post { Title = title, BlogId = blogId };
        database.Posts.Add(post);
        await database.SaveChangesAsync(cancellationToken);
        return post;
    }
}
