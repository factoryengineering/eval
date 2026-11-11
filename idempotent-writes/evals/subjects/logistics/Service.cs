using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public record DepotBody(Guid Id, string Code, int CapacityCubicMeters);
public record DepotResponse(Guid Id, string Code, int CapacityCubicMeters);
public record ConsignmentBody(Guid Id, Guid WarehouseId, string Reference, decimal WeightKg, bool Fragile);
public record ConsignmentResponse(Guid Id, Guid WarehouseId, string Reference, decimal WeightKg, bool Fragile);
public record Stored<T>(T Representation, string ETag, bool Created);

public class ProblemException : Exception
{
    public ProblemException(int status, string title, string? eTag = null) : base(title)
    {
        Status = status;
        ETag = eTag;
    }

    public int Status { get; }
    public string? ETag { get; }

    public static ProblemException BadRequest(string title) => new(400, title);
    public static ProblemException NotFound(string title) => new(404, title);
    public static ProblemException Conflict(string title) => new(409, title);
    public static ProblemException Gone(string title) => new(410, title);
    public static ProblemException PreconditionFailed(string title, string currentETag) => new(412, title, currentETag);
    public static ProblemException PreconditionRequired(string title) => new(428, title);
}

public static class Representation
{
    public static string Hash(params (string Name, object Value)[] fields)
    {
        var json = new StringBuilder("{");
        for (var index = 0; index < fields.Length; index++)
        {
            if (index > 0) json.Append(',');
            AppendString(json, fields[index].Name);
            json.Append(':');
            AppendValue(json, fields[index].Value);
        }
        json.Append('}');
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString()))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(DepotBody body) => Hash(("id", body.Id), ("code", body.Code), ("capacityCubicMeters", body.CapacityCubicMeters));

    public static string Hash(ConsignmentBody body) =>
        Hash(("id", body.Id), ("warehouseId", body.WarehouseId), ("reference", body.Reference), ("weightKg", body.WeightKg), ("fragile", body.Fragile));

    public static string Unquote(string? headerValue) => headerValue?.Trim().Trim('"') ?? "";

    private static void AppendValue(StringBuilder json, object value)
    {
        switch (value)
        {
            case Guid guid: AppendString(json, guid.ToString("D")); break;
            case string text: AppendString(json, text); break;
            case bool flag: json.Append(flag ? "true" : "false"); break;
            case int number: json.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case decimal amount: json.Append(amount.ToString("0.##", CultureInfo.InvariantCulture)); break;
            default: throw new ArgumentException($"Unsupported value type {value.GetType()}.");
        }
    }

    private static void AppendString(StringBuilder json, string text)
    {
        json.Append('"');
        foreach (var character in text.Normalize(NormalizationForm.FormC))
        {
            switch (character)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                case < ' ': json.Append("\\u").Append(((int)character).ToString("x4")); break;
                default: json.Append(character); break;
            }
        }
        json.Append('"');
    }
}

public class DepotService
{
    private readonly LogisticsContext database;

    public DepotService(LogisticsContext database) => this.database = database;

    public async Task<Stored<DepotResponse>> Create(DepotBody body, CancellationToken cancellationToken)
    {
        if (body.Id == Guid.Empty) throw ProblemException.BadRequest("The id is required.");
        Validate(body);
        var hash = Representation.Hash(body);
        var depot = new Depot { PublicId = body.Id, Code = body.Code, CapacityCubicMeters = body.CapacityCubicMeters, ETag = hash, CreationHash = hash };
        database.Depots.Add(depot);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return new Stored<DepotResponse>(ToResponse(depot), depot.ETag, true);
        }
        catch (DbUpdateException)
        {
            database.Entry(depot).State = EntityState.Detached;
            var existingQuery = from stored in database.Depots.IgnoreQueryFilters()
                                where stored.PublicId == body.Id
                                select stored;
            var existing = await existingQuery.SingleOrDefaultAsync(cancellationToken);
            if (existing is null) throw;
            if (existing.CreationHash != hash) throw ProblemException.Conflict("A warehouse with this id exists with different content.");
            if (existing.DeletedAt != null) throw ProblemException.Gone("The warehouse was deleted.");
            return new Stored<DepotResponse>(ToResponse(existing), existing.ETag, false);
        }
    }

    public async Task<Stored<DepotResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var depot = await Find(id, cancellationToken);
        return new Stored<DepotResponse>(ToResponse(depot), depot.ETag, false);
    }

    public async Task<string> Replace(Guid id, DepotBody body, string? ifMatch, CancellationToken cancellationToken)
    {
        if (body.Id != id) throw ProblemException.BadRequest("The body id must equal the route id.");
        Validate(body);
        if (ifMatch is null) throw ProblemException.PreconditionRequired("The If-Match header is required.");
        var hash = Representation.Hash(body);
        for (var attempt = 0; ; attempt++)
        {
            var depot = await Find(id, cancellationToken);
            if (depot.ETag == hash) return hash;
            if (ifMatch.Trim() != "*" && Representation.Unquote(ifMatch) != depot.ETag)
                throw ProblemException.PreconditionFailed("The warehouse was changed by someone else.", depot.ETag);
            depot.Code = body.Code;
            depot.CapacityCubicMeters = body.CapacityCubicMeters;
            depot.ETag = hash;
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return hash;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.Entry(depot).State = EntityState.Detached;
            }
            catch (DbUpdateConcurrencyException)
            {
                var currentETag = await CurrentETag(id, cancellationToken);
                throw ProblemException.PreconditionFailed("The warehouse was changed by someone else.", currentETag);
            }
        }
    }

    public async Task Delete(Guid id, string? ifMatch, CancellationToken cancellationToken)
    {
        if (ifMatch is null) throw ProblemException.PreconditionRequired("The If-Match header is required.");
        for (var attempt = 0; ; attempt++)
        {
            var depotQuery = from stored in database.Depots
                             where stored.PublicId == id
                             select stored;
            var depot = await depotQuery.SingleOrDefaultAsync(cancellationToken);
            if (depot is null) return;
            if (ifMatch.Trim() != "*" && Representation.Unquote(ifMatch) != depot.ETag)
                throw ProblemException.PreconditionFailed("The warehouse was changed by someone else.", depot.ETag);
            var deletedAt = DateTime.UtcNow;
            depot.DeletedAt = deletedAt;
            var consignmentQuery = from consignment in database.Consignments
                                   where consignment.DepotId == depot.Id
                                   select consignment;
            var consignments = await consignmentQuery.ToListAsync(cancellationToken);
            foreach (var consignment in consignments) consignment.DeletedAt = deletedAt;
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException)
            {
                var currentETag = await CurrentETag(id, cancellationToken);
                throw ProblemException.PreconditionFailed("The warehouse was changed by someone else.", currentETag);
            }
        }
    }

    private static void Validate(DepotBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Code)) throw ProblemException.BadRequest("The code is required.");
        if (body.CapacityCubicMeters < 1) throw ProblemException.BadRequest("The capacity must be positive.");
    }

    private async Task<Depot> Find(Guid id, CancellationToken cancellationToken)
    {
        var depotQuery = from stored in database.Depots.IgnoreQueryFilters()
                         where stored.PublicId == id
                         select stored;
        var depot = await depotQuery.SingleOrDefaultAsync(cancellationToken);
        if (depot is null) throw ProblemException.NotFound("The warehouse does not exist.");
        if (depot.DeletedAt != null) throw ProblemException.Gone("The warehouse was deleted.");
        return depot;
    }

    private async Task<string> CurrentETag(Guid id, CancellationToken cancellationToken)
    {
        var eTagQuery = from stored in database.Depots
                        where stored.PublicId == id
                        select stored.ETag;
        var eTags = await eTagQuery.ToListAsync(cancellationToken);
        return eTags.SingleOrDefault() ?? "";
    }

    private static DepotResponse ToResponse(Depot depot) => new(depot.PublicId, depot.Code, depot.CapacityCubicMeters);
}

public class ConsignmentService
{
    private readonly LogisticsContext database;

    public ConsignmentService(LogisticsContext database) => this.database = database;

    public async Task<Stored<ConsignmentResponse>> Create(Guid warehouseId, ConsignmentBody body, CancellationToken cancellationToken)
    {
        if (body.Id == Guid.Empty) throw ProblemException.BadRequest("The id is required.");
        if (body.WarehouseId != warehouseId) throw ProblemException.BadRequest("The body warehouseId must equal the route warehouseId.");
        Validate(body);
        var depotQuery = from stored in database.Depots.IgnoreQueryFilters()
                         where stored.PublicId == warehouseId
                         select stored;
        var depot = await depotQuery.SingleOrDefaultAsync(cancellationToken);
        if (depot is null) throw ProblemException.NotFound("The warehouse does not exist.");
        if (depot.DeletedAt != null) throw ProblemException.Gone("The warehouse was deleted.");
        var hash = Representation.Hash(body);
        var consignment = new Consignment { PublicId = body.Id, DepotId = depot.Id, Reference = body.Reference, WeightKg = body.WeightKg, Fragile = body.Fragile, ETag = hash, CreationHash = hash };
        database.Consignments.Add(consignment);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return new Stored<ConsignmentResponse>(ToResponse(consignment, warehouseId), consignment.ETag, true);
        }
        catch (DbUpdateException)
        {
            database.Entry(consignment).State = EntityState.Detached;
            var existingQuery = from stored in database.Consignments.IgnoreQueryFilters()
                                join owner in database.Depots.IgnoreQueryFilters() on stored.DepotId equals owner.Id
                                where stored.PublicId == body.Id
                                select new { Consignment = stored, OwnerPublicId = owner.PublicId };
            var existing = await existingQuery.SingleOrDefaultAsync(cancellationToken);
            if (existing is null) throw;
            if (existing.Consignment.CreationHash != hash) throw ProblemException.Conflict("A shipment with this id exists with different content.");
            if (existing.Consignment.DeletedAt != null) throw ProblemException.Gone("The shipment was deleted.");
            return new Stored<ConsignmentResponse>(ToResponse(existing.Consignment, existing.OwnerPublicId), existing.Consignment.ETag, false);
        }
    }

    public async Task<Stored<ConsignmentResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var (consignment, ownerId) = await Find(id, cancellationToken);
        return new Stored<ConsignmentResponse>(ToResponse(consignment, ownerId), consignment.ETag, false);
    }

    public async Task<string> Replace(Guid id, ConsignmentBody body, string? ifMatch, CancellationToken cancellationToken)
    {
        if (body.Id != id) throw ProblemException.BadRequest("The body id must equal the route id.");
        Validate(body);
        if (ifMatch is null) throw ProblemException.PreconditionRequired("The If-Match header is required.");
        var hash = Representation.Hash(body);
        for (var attempt = 0; ; attempt++)
        {
            var (consignment, ownerId) = await Find(id, cancellationToken);
            if (ownerId != body.WarehouseId) throw ProblemException.Conflict("The warehouse of a shipment cannot be changed.");
            if (consignment.ETag == hash) return hash;
            if (ifMatch.Trim() != "*" && Representation.Unquote(ifMatch) != consignment.ETag)
                throw ProblemException.PreconditionFailed("The shipment was changed by someone else.", consignment.ETag);
            consignment.Reference = body.Reference;
            consignment.WeightKg = body.WeightKg;
            consignment.Fragile = body.Fragile;
            consignment.ETag = hash;
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return hash;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.Entry(consignment).State = EntityState.Detached;
            }
            catch (DbUpdateConcurrencyException)
            {
                var currentETag = await CurrentETag(id, cancellationToken);
                throw ProblemException.PreconditionFailed("The shipment was changed by someone else.", currentETag);
            }
        }
    }

    public async Task Delete(Guid id, string? ifMatch, CancellationToken cancellationToken)
    {
        if (ifMatch is null) throw ProblemException.PreconditionRequired("The If-Match header is required.");
        for (var attempt = 0; ; attempt++)
        {
            var consignmentQuery = from stored in database.Consignments
                                   where stored.PublicId == id
                                   select stored;
            var consignment = await consignmentQuery.SingleOrDefaultAsync(cancellationToken);
            if (consignment is null) return;
            if (ifMatch.Trim() != "*" && Representation.Unquote(ifMatch) != consignment.ETag)
                throw ProblemException.PreconditionFailed("The shipment was changed by someone else.", consignment.ETag);
            consignment.DeletedAt = DateTime.UtcNow;
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException)
            {
                var currentETag = await CurrentETag(id, cancellationToken);
                throw ProblemException.PreconditionFailed("The shipment was changed by someone else.", currentETag);
            }
        }
    }

    private static void Validate(ConsignmentBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Reference)) throw ProblemException.BadRequest("The reference is required.");
        if (body.WeightKg <= 0 || decimal.Round(body.WeightKg, 2) != body.WeightKg) throw ProblemException.BadRequest("The weight must be positive with at most 2 decimals.");
    }

    private async Task<(Consignment Consignment, Guid OwnerId)> Find(Guid id, CancellationToken cancellationToken)
    {
        var consignmentQuery = from stored in database.Consignments.IgnoreQueryFilters()
                               join owner in database.Depots.IgnoreQueryFilters() on stored.DepotId equals owner.Id
                               where stored.PublicId == id
                               select new { Consignment = stored, OwnerPublicId = owner.PublicId };
        var found = await consignmentQuery.SingleOrDefaultAsync(cancellationToken);
        if (found is null) throw ProblemException.NotFound("The shipment does not exist.");
        if (found.Consignment.DeletedAt != null) throw ProblemException.Gone("The shipment was deleted.");
        return (found.Consignment, found.OwnerPublicId);
    }

    private async Task<string> CurrentETag(Guid id, CancellationToken cancellationToken)
    {
        var eTagQuery = from stored in database.Consignments
                        where stored.PublicId == id
                        select stored.ETag;
        var eTags = await eTagQuery.ToListAsync(cancellationToken);
        return eTags.SingleOrDefault() ?? "";
    }

    private static ConsignmentResponse ToResponse(Consignment consignment, Guid warehouseId) =>
        new(consignment.PublicId, warehouseId, consignment.Reference, consignment.WeightKg, consignment.Fragile);
}
