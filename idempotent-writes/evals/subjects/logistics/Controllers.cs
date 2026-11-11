using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/warehouses")]
public class WarehousesController : ControllerBase
{
    private readonly DepotService depots;

    public WarehousesController(DepotService depots) => this.depots = depots;

    [HttpPost]
    public async Task<IActionResult> Create(DepotBody body, CancellationToken cancellationToken)
    {
        var stored = await depots.Create(body, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        return stored.Created ? Created($"/api/warehouses/{stored.Representation.Id}", stored.Representation) : Ok(stored.Representation);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var stored = await depots.Get(id, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        if (Representation.Unquote(Request.Headers.IfNoneMatch.ToString()) == stored.ETag) return StatusCode(304);
        return Ok(stored.Representation);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Replace(Guid id, DepotBody body, CancellationToken cancellationToken)
    {
        var eTag = await depots.Replace(id, body, Header("If-Match"), cancellationToken);
        Response.Headers.ETag = $"\"{eTag}\"";
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await depots.Delete(id, Header("If-Match"), cancellationToken);
        return NoContent();
    }

    private string? Header(string name) => Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null;
}

[ApiController]
public class ShipmentsController : ControllerBase
{
    private readonly ConsignmentService consignments;

    public ShipmentsController(ConsignmentService consignments) => this.consignments = consignments;

    [HttpPost("api/warehouses/{warehouseId:guid}/shipments")]
    public async Task<IActionResult> Create(Guid warehouseId, ConsignmentBody body, CancellationToken cancellationToken)
    {
        var stored = await consignments.Create(warehouseId, body, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        return stored.Created ? Created($"/api/shipments/{stored.Representation.Id}", stored.Representation) : Ok(stored.Representation);
    }

    [HttpGet("api/shipments/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var stored = await consignments.Get(id, cancellationToken);
        Response.Headers.ETag = $"\"{stored.ETag}\"";
        if (Representation.Unquote(Request.Headers.IfNoneMatch.ToString()) == stored.ETag) return StatusCode(304);
        return Ok(stored.Representation);
    }

    [HttpPut("api/shipments/{id:guid}")]
    public async Task<IActionResult> Replace(Guid id, ConsignmentBody body, CancellationToken cancellationToken)
    {
        var eTag = await consignments.Replace(id, body, Header("If-Match"), cancellationToken);
        Response.Headers.ETag = $"\"{eTag}\"";
        return NoContent();
    }

    [HttpDelete("api/shipments/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await consignments.Delete(id, Header("If-Match"), cancellationToken);
        return NoContent();
    }

    private string? Header(string name) => Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null;
}
