using Microsoft.EntityFrameworkCore;

namespace EfFixtures;

public class BlogNotFoundException(int blogId) : Exception($"Blog {blogId} does not exist.")
{
    public int BlogId { get; } = blogId;
}

public class BlogService(BlogContext database)
{
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
