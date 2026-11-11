using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/warehouses")]
public class WarehousesController : ControllerBase
{
    private readonly DepotService depots;

    public WarehousesController(DepotService depots) => this.depots = depots;

    [HttpPost]
    [ProducesResponseType<DepotResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<DepotResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Create(DepotBody body, CancellationToken cancellationToken)
    {
        var stored = await depots.Create(body, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        return stored.Created ? Created($"/api/warehouses/{stored.Representation.Id}", stored.Representation) : Ok(stored.Representation);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<DepotResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var stored = await depots.Get(id, cancellationToken);
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
    public async Task<IActionResult> Replace(Guid id, DepotBody body, [FromHeader(Name = "If-Match"), Required] string ifMatch, CancellationToken cancellationToken)
    {
        var eTag = await depots.Replace(id, body, ifMatch, cancellationToken);
        Response.Headers.ETag = $"\"{eTag}\"";
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired, "application/problem+json")]
    public async Task<IActionResult> Delete(Guid id, [FromHeader(Name = "If-Match"), Required] string ifMatch, CancellationToken cancellationToken)
    {
        await depots.Delete(id, ifMatch, cancellationToken);
        return NoContent();
    }
}

[ApiController]
public class ShipmentsController : ControllerBase
{
    private readonly ConsignmentService consignments;

    public ShipmentsController(ConsignmentService consignments) => this.consignments = consignments;

    [HttpPost("api/warehouses/{warehouseId:guid}/shipments")]
    [ProducesResponseType<ConsignmentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ConsignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Create(Guid warehouseId, ConsignmentBody body, CancellationToken cancellationToken)
    {
        var stored = await consignments.Create(warehouseId, body, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        return stored.Created ? Created($"/api/shipments/{stored.Representation.Id}", stored.Representation) : Ok(stored.Representation);
    }

    [HttpGet("api/shipments/{id:guid}")]
    [ProducesResponseType<ConsignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var stored = await consignments.Get(id, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        if (Representation.Unquote(Request.Headers.IfNoneMatch.ToString()) == stored.ETag) return StatusCode(304);
        return Ok(stored.Representation);
    }

    [HttpPut("api/shipments/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired, "application/problem+json")]
    public async Task<IActionResult> Replace(Guid id, ConsignmentBody body, [FromHeader(Name = "If-Match"), Required] string ifMatch, CancellationToken cancellationToken)
    {
        var eTag = await consignments.Replace(id, body, ifMatch, cancellationToken);
        Response.Headers.ETag = $"\"{eTag}\"";
        return NoContent();
    }

    [HttpDelete("api/shipments/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired, "application/problem+json")]
    public async Task<IActionResult> Delete(Guid id, [FromHeader(Name = "If-Match"), Required] string ifMatch, CancellationToken cancellationToken)
    {
        await consignments.Delete(id, ifMatch, cancellationToken);
        return NoContent();
    }
}
