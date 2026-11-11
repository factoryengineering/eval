using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/support-tickets")]
public class TicketsController : ControllerBase
{
    private readonly TicketService tickets;

    public TicketsController(TicketService tickets) => this.tickets = tickets;

    [HttpPost]
    public async Task<IActionResult> Create(TicketBody body, CancellationToken cancellationToken)
    {
        var stored = await tickets.Create(body, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        return stored.Created ? Created($"/api/support-tickets/{stored.Representation.Id}", stored.Representation) : Ok(stored.Representation);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var stored = await tickets.Get(id, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        if (Representation.Unquote(Request.Headers.IfNoneMatch.ToString()) == stored.ETag) return StatusCode(304);
        return Ok(stored.Representation);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Replace(Guid id, TicketBody body, CancellationToken cancellationToken)
    {
        var eTag = await tickets.Replace(id, body, Header("If-Match"), cancellationToken);
        Response.Headers.ETag = $"\"{eTag}\"";
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await tickets.Delete(id, Header("If-Match"), cancellationToken);
        return NoContent();
    }

    private string? Header(string name) => Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null;
}
