using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/blogs")]
public class BlogsController : ControllerBase
{
    private readonly BlogService blogs;

    public BlogsController(BlogService blogs) => this.blogs = blogs;

    [HttpPost]
    [ProducesResponseType<BlogResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<BlogResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Create(BlogBody body, CancellationToken cancellationToken)
    {
        var stored = await blogs.Create(body, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        return stored.Created ? Created($"/api/blogs/{stored.Representation.Id}", stored.Representation) : Ok(stored.Representation);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<BlogResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var stored = await blogs.Get(id, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        if (Representation.Unquote(Request.Headers.IfNoneMatch.ToString()) == stored.ETag) return StatusCode(304);
        return Ok(stored.Representation);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired, "application/problem+json")]
    public async Task<IActionResult> Replace(Guid id, BlogBody body, [FromHeader(Name = "If-Match"), Required] string ifMatch, CancellationToken cancellationToken)
    {
        var eTag = await blogs.Replace(id, body, ifMatch, cancellationToken);
        Response.Headers.ETag = $"\"{eTag}\"";
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired, "application/problem+json")]
    public async Task<IActionResult> Delete(Guid id, [FromHeader(Name = "If-Match"), Required] string ifMatch, CancellationToken cancellationToken)
    {
        await blogs.Delete(id, ifMatch, cancellationToken);
        return NoContent();
    }
}

[ApiController]
public class PostsController : ControllerBase
{
    private readonly PostService posts;

    public PostsController(PostService posts) => this.posts = posts;

    [HttpPost("api/blogs/{blogId:guid}/posts")]
    [ProducesResponseType<PostResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<PostResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Create(Guid blogId, PostBody body, CancellationToken cancellationToken)
    {
        var stored = await posts.Create(blogId, body, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        return stored.Created ? Created($"/api/posts/{stored.Representation.Id}", stored.Representation) : Ok(stored.Representation);
    }

    [HttpGet("api/posts/{id:guid}")]
    [ProducesResponseType<PostResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var stored = await posts.Get(id, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        if (Representation.Unquote(Request.Headers.IfNoneMatch.ToString()) == stored.ETag) return StatusCode(304);
        return Ok(stored.Representation);
    }

    [HttpPut("api/posts/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired, "application/problem+json")]
    public async Task<IActionResult> Replace(Guid id, PostBody body, [FromHeader(Name = "If-Match"), Required] string ifMatch, CancellationToken cancellationToken)
    {
        var eTag = await posts.Replace(id, body, ifMatch, cancellationToken);
        Response.Headers.ETag = $"\"{eTag}\"";
        return NoContent();
    }

    [HttpDelete("api/posts/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired, "application/problem+json")]
    public async Task<IActionResult> Delete(Guid id, [FromHeader(Name = "If-Match"), Required] string ifMatch, CancellationToken cancellationToken)
    {
        await posts.Delete(id, ifMatch, cancellationToken);
        return NoContent();
    }
}
