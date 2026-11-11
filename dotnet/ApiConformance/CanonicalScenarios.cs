using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiConformance;

public sealed record RawField(string Name, string Json);

public sealed partial class ScenarioContext
{
    public IReadOnlyList<RawField> RawFields(string idText, string? parentText, IReadOnlyList<JsonElement> values)
    {
        var fields = new List<RawField> { new("id", Text(idText)) };
        if (Domain.Parent is { } parent && parentText is not null) fields.Add(new(parent.ParentBodyField, Text(parentText)));
        for (var index = 0; index < Domain.WritableFields.Count; index++)
            fields.Add(new(Domain.WritableFields[index].Name, values[index].GetRawText()));
        return fields;
    }

    public IReadOnlyList<RawField> RawFields(Guid id, Guid? parentId, IReadOnlyList<JsonElement> values) =>
        RawFields(id.ToString("D"), parentId?.ToString("D"), values);

    public static string Text(string text) => JsonSerializer.Serialize(text);

    public static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static string Compose(IEnumerable<RawField> fields, string separator = ",", string colon = ":") =>
        "{" + string.Join(separator, fields.Select(f => $"{Text(f.Name)}{colon}{f.Json}")) + "}";

    public Task<HttpTrace> Post(Guid? parentId, string body) => Send(HttpMethod.Post, CreateRoute(parentId), body);

    public int FreeTextField => Domain.WritableFields.ToList().FindIndex(f => f.JsonType == "string");

    public IReadOnlyList<JsonElement> WithFreeText(IReadOnlyList<JsonElement> values, Func<string, string> change) =>
        WithField(values, FreeTextField, Element(Text(change(values[FreeTextField].GetString()!))));

    public IReadOnlyList<JsonElement> WithField(IReadOnlyList<JsonElement> values, int field, JsonElement value) =>
        values.Select((existing, index) => index == field ? value : existing).ToList();

    public void RequireETagIs(HttpTrace trace, string expected, string what) =>
        Require(trace.Header("ETag") == expected, $"{what}: the ETag must be the canonical hash {expected}, got {trace.Header("ETag") ?? "none"}.");
}

public static partial class ProtocolScenarios
{
    public const string CanonicalGroup = "canonical";

    private static readonly Regex ETagShape = new("^\"[A-Za-z0-9_-]{43}\"$");

    private static bool IsFractional(FieldDescriptor field) => field.ClrType is "System.Decimal" or "System.Double" or "System.Single";

    public static IReadOnlyList<Scenario> Canonical(DomainDescriptor domain)
    {
        var scenarios = new List<Scenario>
        {
            new("canonical-property-order-does-not-change-etag", CanonicalGroup, PropertyOrderDoesNotChangeETag),
            new("canonical-whitespace-does-not-change-etag", CanonicalGroup, WhitespaceDoesNotChangeETag),
            new("canonical-uppercase-guids-same-etag-and-identity", CanonicalGroup, UppercaseGuidsSameETagAndIdentity),
            new("canonical-different-ids-have-different-etags", CanonicalGroup, DifferentIdsHaveDifferentETags),
            new("canonical-changing-any-writable-field-changes-etag", CanonicalGroup, ChangingAnyFieldChangesETag),
            new("canonical-etag-shape-is-quoted-base64url-sha256", CanonicalGroup, ETagShapeIsQuotedBase64Url),
            new("canonical-etag-is-stable-across-gets-and-no-op-put", CanonicalGroup, ETagIsStable)
        };
        if (domain.WritableFields.Any(f => f.JsonType == "string"))
        {
            scenarios.Add(new("canonical-nfd-strings-same-etag-as-nfc-and-replay-succeeds", CanonicalGroup, NfdSameAsNfc));
            scenarios.Add(new("canonical-escaped-characters-hash-and-round-trip", CanonicalGroup, EscapedCharactersRoundTrip));
        }
        if (domain.WritableFields.Any(IsFractional))
            scenarios.Add(new("canonical-number-spellings-same-etag", CanonicalGroup, NumberSpellingsSameETag));
        return scenarios;
    }

    private static async Task<(Guid? ParentId, Guid Id, IReadOnlyList<JsonElement> Values, string Expected)> Prepare(ScenarioContext context, int sample = 0)
    {
        var parentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var values = context.Values(sample);
        return (parentId, id, values, ScenarioContext.Quoted(context.Hash(id, parentId, values)));
    }

    private static async Task RequireCreateAndReplay(ScenarioContext context, Guid? parentId, Guid id, IReadOnlyList<JsonElement> values, string createBody, string replayBody, string what)
    {
        var expected = ScenarioContext.Quoted(context.Hash(id, parentId, values));
        var created = await context.Post(parentId, createBody);
        context.RequireStatus(created, 201, $"Create with {what}");
        context.RequireETagIs(created, expected, $"Create with {what}");
        var replay = await context.Post(parentId, replayBody);
        context.RequireSuccess(replay, $"Replay of the create in another form ({what})");
        context.RequireETagIs(replay, expected, $"Replay in another form ({what})");
        var read = await context.Get(id);
        context.RequireStatus(read, 200, "GET");
        context.RequireETagIs(read, expected, "GET");
    }

    private static async Task PropertyOrderDoesNotChangeETag(ScenarioContext context)
    {
        var (parentId, id, values, _) = await Prepare(context);
        var fields = context.RawFields(id, parentId, values);

        await RequireCreateAndReplay(context, parentId, id, values, ScenarioContext.Compose(fields.Reverse()), ScenarioContext.Compose(fields), "reversed property order");
    }

    private static async Task WhitespaceDoesNotChangeETag(ScenarioContext context)
    {
        var (parentId, id, values, _) = await Prepare(context);
        var fields = context.RawFields(id, parentId, values);
        var indented = "  \r\n\t" + ScenarioContext.Compose(fields, " ,\r\n\t\t", " :  ").Replace("{", "{\r\n\t\t ") + "\r\n ";

        await RequireCreateAndReplay(context, parentId, id, values, indented, ScenarioContext.Compose(fields), "indentation and extra whitespace");
    }

    private static async Task NfdSameAsNfc(ScenarioContext context)
    {
        var (parentId, id, values, _) = await Prepare(context);
        var nfc = context.WithFreeText(values, text => text + " caf\u00e9 \u00c5");
        var nfd = context.WithFreeText(values, text => text + " cafe\u0301 A\u030a");

        await RequireCreateAndReplay(context, parentId, id, nfc, ScenarioContext.Compose(context.RawFields(id, parentId, nfd)), ScenarioContext.Compose(context.RawFields(id, parentId, nfc)), "NFD strings");
    }

    private static async Task UppercaseGuidsSameETagAndIdentity(ScenarioContext context)
    {
        var (parentId, id, values, expected) = await Prepare(context);
        var upperId = id.ToString("D").ToUpperInvariant();
        var upperParent = parentId?.ToString("D").ToUpperInvariant();
        var route = parentId is null ? context.CreateRoute(null) : context.CreateRoute(parentId).Replace(parentId.ToString()!, upperParent);
        var body = ScenarioContext.Compose(context.RawFields(upperId, upperParent, values));

        var created = await context.Send(HttpMethod.Post, route, body);

        context.RequireStatus(created, 201, "Create with uppercase GUIDs");
        context.RequireETagIs(created, expected, "Create with uppercase GUIDs");
        var read = await context.Get(id);
        context.RequireStatus(read, 200, "GET of the lowercase route after an uppercase create");
        context.RequireETagIs(read, expected, "GET with the lowercase route");
        var upperRead = await context.Send(HttpMethod.Get, $"{context.Domain.CollectionRoute}/{upperId}");
        context.RequireStatus(upperRead, 200, "GET of the uppercase route");
        var replay = await context.Post(parentId, ScenarioContext.Compose(context.RawFields(id, parentId, values)));
        context.RequireSuccess(replay, "Replay of an uppercase create with lowercase GUIDs");
        context.RequireETagIs(replay, expected, "Replay with lowercase GUIDs");
    }

    private static async Task NumberSpellingsSameETag(ScenarioContext context)
    {
        var index = context.Domain.WritableFields.ToList().FindIndex(IsFractional);
        var number = context.Values(0)[index].GetDecimal();
        var normal = CanonicalHash.Number(number);
        var spellings = new[]
        {
            normal,
            normal.Contains('.') ? normal + "0" : normal + ".0",
            number.ToString("E6", CultureInfo.InvariantCulture),
            "0.5"
        };
        var parentId = await context.CreateParent();
        foreach (var spelling in spellings)
        {
            var id = Guid.NewGuid();
            var values = context.WithField(context.Values(0), index, ScenarioContext.Element(spelling));
            var expected = ScenarioContext.Quoted(context.Hash(id, parentId, values));

            var created = await context.Post(parentId, ScenarioContext.Compose(context.RawFields(id, parentId, values)));

            context.RequireStatus(created, 201, $"Create with the number spelled {spelling}");
            context.RequireETagIs(created, expected, $"Number spelled {spelling}");
        }
    }

    private static async Task DifferentIdsHaveDifferentETags(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var first = await context.CreateResource(parentId);
        var second = await context.CreateResource(parentId);

        context.Require(first.ETag != second.ETag, "Resources with the same writable values and different ids must have different ETags.");
        context.RequireETagIs(first.Created, ScenarioContext.Quoted(context.Hash(first.Id, parentId, first.Values)), "First resource");
        context.RequireETagIs(second.Created, ScenarioContext.Quoted(context.Hash(second.Id, parentId, second.Values)), "Second resource");
    }

    private static async Task ChangingAnyFieldChangesETag(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var current = resource.ETag;
        for (var index = 0; index < context.Domain.WritableFields.Count; index++)
        {
            var field = context.Domain.WritableFields[index];
            var changed = context.WithField(resource.Values, index, field.Samples.First(s => s.GetRawText() != resource.Values[index].GetRawText()));
            var expected = ScenarioContext.Quoted(context.Hash(resource.Id, resource.ParentId, changed));

            var put = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, changed), current);

            context.RequireStatus(put, 204, $"PUT changing '{field.Name}'");
            context.RequireETagIs(put, expected, $"PUT changing '{field.Name}'");
            context.Require(expected != current, $"Changing '{field.Name}' must change the ETag.");
            var read = await context.Get(resource.Id);
            context.RequireETagIs(read, expected, $"GET after changing '{field.Name}'");
            current = expected;
        }
    }

    private static async Task ETagShapeIsQuotedBase64Url(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var read = await context.Get(resource.Id);
        var changed = context.Values(1);
        var put = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, changed), resource.ETag);

        foreach (var (what, trace) in new[] { ("create", resource.Created), ("GET", read), ("PUT", put) })
            context.Require(trace.Header("ETag") is { } etag && ETagShape.IsMatch(etag), $"The {what} ETag must be quoted base64url without padding (43 characters), got {trace.Header("ETag") ?? "none"}.");
    }

    private static async Task EscapedCharactersRoundTrip(ScenarioContext context)
    {
        const string special = "q\"uote back\\slash bell\u0007 emoji\ud83d\ude00 mark x\u0301 tab\t end";
        var (parentId, id, values, _) = await Prepare(context);
        var index = context.FreeTextField;
        values = context.WithFreeText(values, _ => special);
        var expected = ScenarioContext.Quoted(context.Hash(id, parentId, values));

        var created = await context.Post(parentId, ScenarioContext.Compose(context.RawFields(id, parentId, values)));

        context.RequireStatus(created, 201, "Create with characters that need escaping");
        context.RequireETagIs(created, expected, "Create with characters that need escaping");
        var read = await context.Get(id);
        context.RequireStatus(read, 200, "GET");
        using var document = JsonDocument.Parse(read.ResponseBody);
        var name = context.Domain.WritableFields[index].Name;
        context.Require(document.RootElement.TryGetProperty(name, out var actual) && actual.GetString() == special, $"GET must return '{name}' unchanged.");
        context.RequireETagIs(read, expected, "GET");
    }

    private static async Task ETagIsStable(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var first = await context.Get(resource.Id);
        var second = await context.Get(resource.Id);
        var put = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, resource.Values), resource.ETag);
        var after = await context.Get(resource.Id);

        context.RequireETagIs(first, resource.ETag, "First GET");
        context.RequireETagIs(second, resource.ETag, "Second GET");
        context.RequireStatus(put, 204, "No-op PUT");
        context.RequireETagIs(put, resource.ETag, "No-op PUT");
        context.RequireETagIs(after, resource.ETag, "GET after the no-op PUT");
    }
}
