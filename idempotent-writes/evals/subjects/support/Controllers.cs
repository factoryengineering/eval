using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/support-tickets")]
public class TicketsController : ControllerBase
{
    private readonly TicketService tickets;

    public TicketsController(TicketService tickets) => this.tickets = tickets;

    [HttpPost]
    [ProducesResponseType<TicketResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<TicketResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Create(TicketBody body, CancellationToken cancellationToken)
    {
        var stored = await tickets.Create(body, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        return stored.Created ? Created($"/api/support-tickets/{stored.Representation.Id}", stored.Representation) : Ok(stored.Representation);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<TicketResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var stored = await tickets.Get(id, cancellationToken);
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
    public async Task<IActionResult> Replace(Guid id, TicketBody body, [FromHeader(Name = "If-Match"), Required] string ifMatch, CancellationToken cancellationToken)
    {
        var eTag = await tickets.Replace(id, body, ifMatch, cancellationToken);
        Response.Headers.ETag = $"\"{eTag}\"";
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired, "application/problem+json")]
    public async Task<IActionResult> Delete(Guid id, [FromHeader(Name = "If-Match"), Required] string ifMatch, CancellationToken cancellationToken)
    {
        await tickets.Delete(id, ifMatch, cancellationToken);
        return NoContent();
    }
}
