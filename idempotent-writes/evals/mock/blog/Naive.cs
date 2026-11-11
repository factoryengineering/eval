using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class Blog
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    [JsonIgnore]
    public List<Post> Posts { get; set; } = new();
}

public class Post
{
    public Guid Id { get; set; }
    public Guid BlogId { get; set; }
    [JsonIgnore]
    public Blog? Blog { get; set; }
    public string Title { get; set; } = "";
    public int Views { get; set; }
}

public class BlogContext : DbContext
{
    public BlogContext(DbContextOptions<BlogContext> options) : base(options) { }
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();
}

public static class AppSetup
{
    public static void ConfigureServices(IServiceCollection services, string sqliteConnectionString) =>
        services.AddDbContext<BlogContext>(options => options.UseSqlite(sqliteConnectionString));
}

[ApiController]
[Route("api/blogs")]
public class BlogsController : ControllerBase
{
    private readonly BlogContext db;

    public BlogsController(BlogContext db) => this.db = db;

    [HttpPost]
    public async Task<ActionResult<Blog>> Create(Blog blog)
    {
        blog.Id = Guid.NewGuid();
        db.Blogs.Add(blog);
        await db.SaveChangesAsync();
        return Created($"/api/blogs/{blog.Id}", blog);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Blog>> Get(Guid id) => await db.Blogs.FindAsync(id) is { } blog ? blog : NotFound();

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, Blog update)
    {
        var blog = await db.Blogs.FindAsync(id);
        if (blog is null) return NotFound();
        blog.Name = update.Name;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var blog = await db.Blogs.FindAsync(id);
        if (blog is null) return NotFound();
        db.Blogs.Remove(blog);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

[ApiController]
public class PostsController : ControllerBase
{
    private readonly BlogContext db;

    public PostsController(BlogContext db) => this.db = db;

    [HttpPost("api/blogs/{blogId}/posts")]
    public async Task<ActionResult<Post>> Create(Guid blogId, Post post)
    {
        if (await db.Blogs.FindAsync(blogId) is null) return NotFound();
        post.Id = Guid.NewGuid();
        post.BlogId = blogId;
        db.Posts.Add(post);
        await db.SaveChangesAsync();
        return Created($"/api/posts/{post.Id}", post);
    }

    [HttpGet("api/posts/{id}")]
    public async Task<ActionResult<Post>> Get(Guid id)
    {
        var post = await db.Posts.FindAsync(id);
        if (post is null) return NotFound();
        post.Views++;
        await db.SaveChangesAsync();
        return post;
    }

    [HttpPut("api/posts/{id}")]
    public async Task<IActionResult> Update(Guid id, Post update)
    {
        var post = await db.Posts.FindAsync(id);
        if (post is null) return NotFound();
        post.Title = update.Title;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("api/posts/{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var post = await db.Posts.FindAsync(id);
        if (post is null) return NotFound();
        db.Posts.Remove(post);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
