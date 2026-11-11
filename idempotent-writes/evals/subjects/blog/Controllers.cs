using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/blogs")]
public class BlogsController : ControllerBase
{
    private readonly BlogService blogs;

    public BlogsController(BlogService blogs) => this.blogs = blogs;

    [HttpPost]
    public async Task<IActionResult> Create(BlogBody body, CancellationToken cancellationToken)
    {
        var stored = await blogs.Create(body, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        return stored.Created ? Created($"/api/blogs/{stored.Representation.Id}", stored.Representation) : Ok(stored.Representation);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var stored = await blogs.Get(id, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        if (Representation.Unquote(Request.Headers.IfNoneMatch.ToString()) == stored.ETag) return StatusCode(304);
        return Ok(stored.Representation);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Replace(Guid id, BlogBody body, CancellationToken cancellationToken)
    {
        var eTag = await blogs.Replace(id, body, Header("If-Match"), cancellationToken);
        Response.Headers.ETag = $"\"{eTag}\"";
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await blogs.Delete(id, Header("If-Match"), cancellationToken);
        return NoContent();
    }

    private string? Header(string name) => Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null;
}

[ApiController]
public class PostsController : ControllerBase
{
    private readonly PostService posts;

    public PostsController(PostService posts) => this.posts = posts;

    [HttpPost("api/blogs/{blogId:guid}/posts")]
    public async Task<IActionResult> Create(Guid blogId, PostBody body, CancellationToken cancellationToken)
    {
        var stored = await posts.Create(blogId, body, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        return stored.Created ? Created($"/api/posts/{stored.Representation.Id}", stored.Representation) : Ok(stored.Representation);
    }

    [HttpGet("api/posts/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var stored = await posts.Get(id, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        if (Representation.Unquote(Request.Headers.IfNoneMatch.ToString()) == stored.ETag) return StatusCode(304);
        return Ok(stored.Representation);
    }

    [HttpPut("api/posts/{id:guid}")]
    public async Task<IActionResult> Replace(Guid id, PostBody body, CancellationToken cancellationToken)
    {
        var eTag = await posts.Replace(id, body, Header("If-Match"), cancellationToken);
        Response.Headers.ETag = $"\"{eTag}\"";
        return NoContent();
    }

    [HttpDelete("api/posts/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await posts.Delete(id, Header("If-Match"), cancellationToken);
        return NoContent();
    }

    private string? Header(string name) => Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null;
}
