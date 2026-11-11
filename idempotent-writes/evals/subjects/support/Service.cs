using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public record TicketBody(Guid Id, string Subject, int Priority, bool Escalated, string Category);
public record TicketResponse(Guid Id, string Subject, int Priority, bool Escalated, string Category);
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

    public static string Hash(TicketBody body) => Hash(("id", body.Id), ("subject", body.Subject), ("priority", body.Priority), ("escalated", body.Escalated), ("category", body.Category));

    public static string Unquote(string? headerValue) => headerValue?.Trim().Trim('"') ?? "";

    private static void AppendValue(StringBuilder json, object value)
    {
        switch (value)
        {
            case Guid guid: AppendString(json, guid.ToString("D")); break;
            case string text: AppendString(json, text); break;
            case bool flag: json.Append(flag ? "true" : "false"); break;
            case int number: json.Append(number.ToString(CultureInfo.InvariantCulture)); break;
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

public class TicketService
{
    private static readonly string[] Categories = ["billing", "hardware", "access", "other"];

    private readonly SupportContext database;

    public TicketService(SupportContext database) => this.database = database;

    public async Task<Stored<TicketResponse>> Create(TicketBody body, CancellationToken cancellationToken)
    {
        if (body.Id == Guid.Empty) throw ProblemException.BadRequest("The id is required.");
        Validate(body);
        var hash = Representation.Hash(body);
        var ticket = new Ticket { PublicId = body.Id, Subject = body.Subject, Priority = body.Priority, Escalated = body.Escalated, Category = body.Category, ETag = hash, CreationHash = hash };
        database.Tickets.Add(ticket);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return new Stored<TicketResponse>(ToResponse(ticket), ticket.ETag, true);
        }
        catch (DbUpdateException)
        {
            database.Entry(ticket).State = EntityState.Detached;
            var existingQuery = from stored in database.Tickets.IgnoreQueryFilters()
                                where stored.PublicId == body.Id
                                select stored;
            var existing = await existingQuery.SingleOrDefaultAsync(cancellationToken);
            if (existing is null) throw;
            if (existing.CreationHash != hash) throw ProblemException.Conflict("A ticket with this id exists with different content.");
            if (existing.DeletedAt != null) throw ProblemException.Gone("The ticket was deleted.");
            return new Stored<TicketResponse>(ToResponse(existing), existing.ETag, false);
        }
    }

    public async Task<Stored<TicketResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var ticket = await Find(id, cancellationToken);
        var viewedAt = DateTime.UtcNow;
        var viewedQuery = from stored in database.Tickets
                          where stored.PublicId == id
                          select stored;
        await viewedQuery.ExecuteUpdateAsync(setters => setters
            .SetProperty(viewed => viewed.ViewCount, viewed => viewed.ViewCount + 1)
            .SetProperty(viewed => viewed.LastViewedAt, viewedAt), cancellationToken);
        return new Stored<TicketResponse>(ToResponse(ticket), ticket.ETag, false);
    }

    public async Task<string> Replace(Guid id, TicketBody body, string? ifMatch, CancellationToken cancellationToken)
    {
        if (body.Id != id) throw ProblemException.BadRequest("The body id must equal the route id.");
        Validate(body);
        if (ifMatch is null) throw ProblemException.PreconditionRequired("The If-Match header is required.");
        var hash = Representation.Hash(body);
        for (var attempt = 0; ; attempt++)
        {
            var ticket = await Find(id, cancellationToken);
            if (ticket.ETag == hash) return hash;
            if (ifMatch.Trim() != "*" && Representation.Unquote(ifMatch) != ticket.ETag)
                throw ProblemException.PreconditionFailed("The ticket was changed by someone else.", ticket.ETag);
            ticket.Subject = body.Subject;
            ticket.Priority = body.Priority;
            ticket.Escalated = body.Escalated;
            ticket.Category = body.Category;
            ticket.ETag = hash;
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return hash;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.Entry(ticket).State = EntityState.Detached;
            }
            catch (DbUpdateConcurrencyException)
            {
                var currentETag = await CurrentETag(id, cancellationToken);
                throw ProblemException.PreconditionFailed("The ticket was changed by someone else.", currentETag);
            }
        }
    }

    public async Task Delete(Guid id, string? ifMatch, CancellationToken cancellationToken)
    {
        if (ifMatch is null) throw ProblemException.PreconditionRequired("The If-Match header is required.");
        for (var attempt = 0; ; attempt++)
        {
            var ticketQuery = from stored in database.Tickets
                              where stored.PublicId == id
                              select stored;
            var ticket = await ticketQuery.SingleOrDefaultAsync(cancellationToken);
            if (ticket is null) return;
            if (ifMatch.Trim() != "*" && Representation.Unquote(ifMatch) != ticket.ETag)
                throw ProblemException.PreconditionFailed("The ticket was changed by someone else.", ticket.ETag);
            ticket.DeletedAt = DateTime.UtcNow;
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
                throw ProblemException.PreconditionFailed("The ticket was changed by someone else.", currentETag);
            }
        }
    }

    private static void Validate(TicketBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Subject)) throw ProblemException.BadRequest("The subject is required.");
        if (body.Priority is < 1 or > 5) throw ProblemException.BadRequest("The priority must be between 1 and 5.");
        if (!Categories.Contains(body.Category)) throw ProblemException.BadRequest("The category is not recognised.");
    }

    private async Task<Ticket> Find(Guid id, CancellationToken cancellationToken)
    {
        var ticketQuery = from stored in database.Tickets.IgnoreQueryFilters()
                          where stored.PublicId == id
                          select stored;
        var ticket = await ticketQuery.SingleOrDefaultAsync(cancellationToken);
        if (ticket is null) throw ProblemException.NotFound("The ticket does not exist.");
        if (ticket.DeletedAt != null) throw ProblemException.Gone("The ticket was deleted.");
        return ticket;
    }

    private async Task<string> CurrentETag(Guid id, CancellationToken cancellationToken)
    {
        var eTagQuery = from stored in database.Tickets
                        where stored.PublicId == id
                        select stored.ETag;
        var eTags = await eTagQuery.ToListAsync(cancellationToken);
        return eTags.SingleOrDefault() ?? "";
    }

    private static TicketResponse ToResponse(Ticket ticket) => new(ticket.PublicId, ticket.Subject, ticket.Priority, ticket.Escalated, ticket.Category);
}
