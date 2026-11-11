using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiConformance;

public sealed record ScenarioResult(string Name, bool Passed, string? Failure, string Group, IReadOnlyList<HttpTrace>? Traces = null);

public sealed record Resource(Guid Id, Guid? ParentId, IReadOnlyList<JsonElement> Values, string ETag, HttpTrace Created);

public sealed record Scenario(string Name, string Group, Func<ScenarioContext, Task> Run);

public sealed class ScenarioFailure(string message) : Exception(message);

public sealed partial class ScenarioContext(ApiHost host, DomainDescriptor domain)
{
    private readonly StringBuilder transcript = new();

    public DomainDescriptor Domain { get; } = domain;
    public string Transcript => transcript.ToString();
    public List<HttpTrace> Traces { get; } = [];

    public async Task<HttpTrace> Send(HttpMethod method, string uri, string? json = null, IDictionary<string, string>? headers = null)
    {
        var trace = await HttpTrace.Send(Host.Client, method, uri, json, headers);
        transcript.AppendLine(trace.ToString());
        Traces.Add(trace);
        return trace;
    }

    public ApiHost Host { get; } = host;

    public string ItemRoute(Guid id) => $"{Domain.CollectionRoute}/{id}";

    public static IDictionary<string, string> Headers(string? ifMatch = null, string? ifNoneMatch = null)
    {
        var headers = new Dictionary<string, string>();
        if (ifMatch is not null) headers["If-Match"] = ifMatch;
        if (ifNoneMatch is not null) headers["If-None-Match"] = ifNoneMatch;
        return headers;
    }

    public const string StaleETag = "\"stale-etag\"";

    public static string Quoted(string hash) => $"\"{hash}\"";

    public Task<HttpTrace> Get(Guid id, string? ifNoneMatch = null) => Send(HttpMethod.Get, ItemRoute(id), null, Headers(null, ifNoneMatch));

    public Task<HttpTrace> Put(Guid id, string body, string? ifMatch) => Send(HttpMethod.Put, ItemRoute(id), body, Headers(ifMatch));

    public Task<HttpTrace> Delete(string route, string? ifMatch) => Send(HttpMethod.Delete, route, null, Headers(ifMatch));

    public async Task<Resource> CreateResource(Guid? parentId = null, int sample = 0)
    {
        parentId ??= await CreateParent();
        var id = Guid.NewGuid();
        var values = Values(sample);
        var created = await Send(HttpMethod.Post, CreateRoute(parentId), Body(id, parentId, values));
        RequireStatus(created, 201, "Creating the resource");
        return new Resource(id, parentId, values, RequireQuotedETag(created, "Creating the resource"), created);
    }

    public async Task<string> ParentETag(Guid parentId)
    {
        var read = await Send(HttpMethod.Get, $"{Domain.Parent!.CollectionRoute}/{parentId}");
        RequireStatus(read, 200, "Reading the parent");
        return RequireQuotedETag(read, "Reading the parent");
    }

    public void RequireNoServerOwnedField(HttpTrace trace, string field, string what)
    {
        using var document = JsonDocument.Parse(trace.ResponseBody.Length == 0 ? "{}" : trace.ResponseBody);
        Require(!document.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase)),
            $"{what}: the representation must not contain the server-owned field '{field}'.");
    }

    public string CreateRoute(Guid? parentId) =>
        Domain.Parent is { } parent ? Regex.Replace(parent.ChildCollectionRoute, @"\{[^}/]+\}", parentId.ToString()!) : Domain.CollectionRoute;

    public IReadOnlyList<JsonElement> Values(int sample) => Domain.WritableFields.Select(f => f.Samples[sample % f.Samples.Count]).ToList();

    public string Body(Guid? id, Guid? parentId, IReadOnlyList<JsonElement> values, string? omitField = null)
    {
        var fields = new List<KeyValuePair<string, object>>();
        if (id is { } idValue) fields.Add(new("id", idValue));
        if (Domain.Parent is { } parent && parentId is { } parentValue) fields.Add(new(parent.ParentBodyField, parentValue));
        for (var index = 0; index < Domain.WritableFields.Count; index++)
            if (Domain.WritableFields[index].Name != omitField)
                fields.Add(new(Domain.WritableFields[index].Name, values[index]));
        return JsonSerializer.Serialize(fields.ToDictionary(f => f.Key, f => f.Value is Guid guid ? guid.ToString("D") : f.Value is JsonElement element ? element : f.Value));
    }

    public string Hash(Guid id, Guid? parentId, IReadOnlyList<JsonElement> values) => CanonicalHash.Compute(Domain, id, parentId, values);

    public async Task<Guid?> CreateParent()
    {
        if (Domain.Parent is not { } parent) return null;
        var id = Guid.NewGuid();
        var trace = await Send(HttpMethod.Post, parent.CollectionRoute, ParentBody(id));
        Require(trace.StatusCode == 201, "Creating the parent resource should return 201.");
        return id;
    }

    public string ParentBody(Guid id) =>
        JsonSerializer.Serialize((Domain.Parent!.WritableFields ?? []).ToDictionary(f => f.Name, f => (object)f.Samples[0]).Append(new("id", id.ToString("D"))).ToDictionary());

    public void Require(bool condition, string message)
    {
        if (!condition) throw new ScenarioFailure(message);
    }

    public void RequireStatus(HttpTrace trace, int expected, string what) =>
        Require(trace.StatusCode == expected, $"{what}: expected {expected} but got {trace.StatusCode}.");

    public void RequireSuccess(HttpTrace trace, string what) =>
        Require(trace.StatusCode is >= 200 and < 300, $"{what}: expected a 2xx status but got {trace.StatusCode}.");

    public void RequireRepresentation(HttpTrace trace, Guid id, Guid? parentId, IReadOnlyList<JsonElement> values, string what)
    {
        using var document = JsonDocument.Parse(trace.ResponseBody.Length == 0 ? "null" : trace.ResponseBody);
        var root = document.RootElement;
        Require(root.ValueKind == JsonValueKind.Object, $"{what}: the body is not a JSON object.");
        foreach (var field in CanonicalHash.Fields(Domain, id, parentId, values))
        {
            var expected = field.Value is Guid guid ? JsonSerializer.SerializeToElement(guid.ToString("D")) : (JsonElement)field.Value;
            Require(root.TryGetProperty(field.Key, out var actual) && SameValue(expected, actual),
                $"{what}: field '{field.Key}' should be {expected.GetRawText()}.");
        }
    }

    private static bool SameValue(JsonElement expected, JsonElement actual) =>
        expected.ValueKind == actual.ValueKind && expected.ValueKind switch
        {
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.Number => expected.GetDecimal() == actual.GetDecimal(),
            _ => true
        };

    public string RequireQuotedETag(HttpTrace trace, string what)
    {
        var etag = trace.Header("ETag");
        Require(etag is not null && Regex.IsMatch(etag, "^\"[^\"]+\"$"), $"{what}: the ETag header must be present and quoted, got {etag ?? "none"}.");
        return etag!;
    }
}

public static partial class ProtocolScenarios
{
    public const string CreateGroup = "create";

    public static IReadOnlyList<Scenario> Create(DomainDescriptor domain)
    {
        var scenarios = new List<Scenario>
        {
            new("create-succeeds", CreateGroup, CreateSucceeds),
            new("create-replay-returns-same-success", CreateGroup, CreateReplayReturnsSameSuccess),
            new("create-replay-after-put-returns-current-representation", CreateGroup, CreateReplayAfterPutReturnsCurrent),
            new("create-same-id-different-body-conflicts", CreateGroup, CreateSameIdDifferentBodyConflicts),
            new("create-missing-id-is-bad-request", CreateGroup, CreateMissingIdIsBadRequest),
            new("create-invalid-body-is-bad-request", CreateGroup, CreateInvalidBodyIsBadRequest),
            new("etag-is-canonical-hash", CreateGroup, ETagIsCanonicalHash)
        };
        if (domain.Parent is not null)
        {
            scenarios.Add(new("child-create-body-parent-must-match-route", CreateGroup, ChildCreateParentMustMatchRoute));
            scenarios.Add(new("child-create-missing-parent-is-not-found", CreateGroup, ChildCreateMissingParentIsNotFound));
            scenarios.Add(new("child-create-same-id-different-parent-conflicts", CreateGroup, ChildCreateSameIdDifferentParentConflicts));
        }
        return scenarios;
    }

    public static IReadOnlyList<Scenario> All(DomainDescriptor domain) =>
        [.. Create(domain), .. Read(domain), .. Replace(domain), .. Delete(domain), .. ServerOwned(domain), .. Race(domain), .. LostResponse(domain), .. Canonical(domain)];

    public static async Task<IReadOnlyList<ScenarioResult>> Run(IEnumerable<Scenario> scenarios, Func<ApiHost> startHost, DomainDescriptor domain)
    {
        var results = await Task.WhenAll(scenarios.Select(scenario => RunOne(scenario, startHost, domain)));
        return results;
    }

    private static async Task<ScenarioResult> RunOne(Scenario scenario, Func<ApiHost> startHost, DomainDescriptor domain)
    {
        await using var host = startHost();
        var context = new ScenarioContext(host, domain);
        try
        {
            await scenario.Run(context);
            return new ScenarioResult(scenario.Name, true, null, scenario.Group, context.Traces);
        }
        catch (Exception failure)
        {
            var message = failure is ScenarioFailure ? failure.Message : failure.ToString();
            return new ScenarioResult(scenario.Name, false, message + Environment.NewLine + context.Transcript, scenario.Group, context.Traces);
        }
    }

    private static async Task CreateSucceeds(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var values = context.Values(0);

        var created = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), context.Body(id, parentId, values));

        context.RequireStatus(created, 201, "First create");
        var location = created.Header("Location");
        context.Require(!string.IsNullOrEmpty(location), "First create must return a Location header.");
        var etag = context.RequireQuotedETag(created, "First create");
        context.RequireRepresentation(created, id, parentId, values, "First create body");
        var read = await context.Send(HttpMethod.Get, location!);
        context.RequireStatus(read, 200, "GET of the Location");
        context.Require(read.Header("ETag") == etag, "GET must return the same ETag as the create response.");
    }

    private static async Task CreateReplayReturnsSameSuccess(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var values = context.Values(0);
        var body = context.Body(id, parentId, values);
        var created = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), body);
        context.RequireStatus(created, 201, "First create");

        var replay = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), body);

        context.RequireSuccess(replay, "Replay of the same create");
        context.RequireRepresentation(replay, id, parentId, values, "Replay body");
        context.Require(replay.Header("ETag") == created.Header("ETag"), "Replay must return the same ETag.");
    }

    private static async Task CreateReplayAfterPutReturnsCurrent(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var body = context.Body(id, parentId, context.Values(0));
        var created = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), body);
        context.RequireStatus(created, 201, "First create");
        var changed = context.Values(1);
        var put = await context.Send(HttpMethod.Put, created.Header("Location")!, context.Body(id, parentId, changed),
            new Dictionary<string, string> { ["If-Match"] = created.Header("ETag")! });
        context.RequireSuccess(put, "PUT with If-Match from the create response");

        var replay = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), body);

        context.RequireSuccess(replay, "Replay after PUT");
        context.RequireRepresentation(replay, id, parentId, changed, "Replay-after-PUT body");
        context.Require(replay.Header("ETag") == $"\"{context.Hash(id, parentId, changed)}\"", "Replay after PUT must return the current ETag.");
    }

    private static async Task CreateSameIdDifferentBodyConflicts(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var created = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), context.Body(id, parentId, context.Values(0)));
        context.RequireStatus(created, 201, "First create");

        var conflict = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), context.Body(id, parentId, context.Values(1)));

        context.RequireStatus(conflict, 409, "Same id with a different body");
    }

    private static async Task CreateMissingIdIsBadRequest(ScenarioContext context)
    {
        var parentId = await context.CreateParent();

        var trace = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), context.Body(null, parentId, context.Values(0)));

        context.RequireStatus(trace, 400, "Create without a client id");
    }

    private static async Task CreateInvalidBodyIsBadRequest(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var omitted = context.Domain.WritableFields[0].Name;

        var trace = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), context.Body(Guid.NewGuid(), parentId, context.Values(0), omitted));

        context.RequireStatus(trace, 400, $"Create without writable field '{omitted}'");
    }

    private static async Task ETagIsCanonicalHash(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var values = context.Values(2);

        var created = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), context.Body(id, parentId, values));

        context.RequireStatus(created, 201, "First create");
        var expected = $"\"{context.Hash(id, parentId, values)}\"";
        context.Require(created.Header("ETag") == expected, $"ETag must be the canonical hash {expected}, got {created.Header("ETag")}.");
    }

    private static async Task ChildCreateParentMustMatchRoute(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var otherParentId = Guid.NewGuid();

        var trace = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), context.Body(Guid.NewGuid(), otherParentId, context.Values(0)));

        context.RequireStatus(trace, 400, "Body parent id differing from the route");
    }

    private static async Task ChildCreateMissingParentIsNotFound(ScenarioContext context)
    {
        var missingParentId = Guid.NewGuid();

        var trace = await context.Send(HttpMethod.Post, context.CreateRoute(missingParentId), context.Body(Guid.NewGuid(), missingParentId, context.Values(0)));

        context.RequireStatus(trace, 404, "Create under a missing parent");
    }

    private static async Task ChildCreateSameIdDifferentParentConflicts(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var otherParentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var values = context.Values(0);
        var created = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), context.Body(id, parentId, values));
        context.RequireStatus(created, 201, "First create");

        var conflict = await context.Send(HttpMethod.Post, context.CreateRoute(otherParentId), context.Body(id, otherParentId, values));

        context.RequireStatus(conflict, 409, "Same id under a different parent");
    }
}
