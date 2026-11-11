using System.Text.Json;

namespace ApiConformance;

public sealed record SpecResult(string Id, bool Passed, string Evidence);

public static class SpecConformance
{
    public const string StatusesId = "spec-declares-observed-statuses";
    public const string BodiesId = "spec-response-bodies-match-schemas";
    public static readonly IReadOnlyList<string> Ids = [StatusesId, BodiesId];

    private const int MaxReported = 5;

    public static IReadOnlyList<SpecResult> Run(OpenApiDoc doc, IEnumerable<HttpTrace> traces)
    {
        var observed = traces.Where(t => t.StatusCode < 500).Select(t => (Trace: t, Operation: doc.Match(t.Method.Method, t.Uri))).ToList();
        var statuses = observed
            .Where(o => o.Operation is null || !o.Operation.Declares(o.Trace.StatusCode))
            .GroupBy(o => $"{o.Trace.Method} {(o.Operation is null ? Path(o.Trace.Uri) + " (no such operation)" : o.Operation.Path)} -> {o.Trace.StatusCode}")
            .Select(g => $"{g.Key}{Environment.NewLine}{g.First().Trace}")
            .ToList();
        var bodies = observed
            .Where(o => o.Operation is { } operation && operation.Declares(o.Trace.StatusCode) && o.Trace.ResponseBody.Length > 0)
            .SelectMany(o => ValidateBody(o.Operation!, o.Trace).Select(v => (Violation: $"{o.Trace.Method} {o.Operation!.Path} -> {o.Trace.StatusCode}: {v}", o.Trace)))
            .GroupBy(v => v.Violation)
            .Select(g => $"{g.Key}{Environment.NewLine}{g.First().Trace}")
            .ToList();
        return [Result(StatusesId, statuses), Result(BodiesId, bodies)];
    }

    public static IReadOnlyList<string> Failing(IEnumerable<SpecResult> results) => results.Where(r => !r.Passed).Select(r => r.Id).Order().ToList();

    private static SpecResult Result(string id, IReadOnlyList<string> violations) =>
        violations.Count == 0 ? new(id, true, "ok") : new(id, false, $"{violations.Count} distinct violation(s):{Environment.NewLine}{string.Join(Environment.NewLine, violations.Take(MaxReported))}");

    private static string Path(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : uri.Split('?')[0];

    private static IEnumerable<string> ValidateBody(OpenApiOperation operation, HttpTrace trace)
    {
        var declared = operation.Response(trace.StatusCode)!;
        var mediaType = (trace.Header("Content-Type") ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (declared.Content.Count == 0) return [$"the response has a body but the document declares no content"];
        if (!declared.Content.TryGetValue(mediaType, out var schema)) return [$"content type '{mediaType}' is not declared (declared: {string.Join(", ", declared.Content.Keys)})"];
        if (schema is null || !mediaType.EndsWith("json")) return [];
        JsonDocument body;
        try { body = JsonDocument.Parse(trace.ResponseBody); }
        catch (JsonException) { return [$"the body is not valid JSON"]; }
        using (body)
            return Validate(body.RootElement, schema, "$", allowExtra: mediaType == "application/problem+json").ToList();
    }

    private static IEnumerable<string> Validate(JsonElement value, OpenApiSchema schema, string path, bool allowExtra)
    {
        var types = schema.NonNullTypes;
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (schema.Types.Count > 0 && !schema.Types.Contains("null")) yield return $"{path} is null but the schema is not nullable";
            yield break;
        }
        if (types.Count > 0 && !types.Any(t => Matches(value, t))) yield return $"{path} is {Describe(value)} but the schema allows {string.Join("|", types)}";
        else if (value.ValueKind == JsonValueKind.String && schema.Format == "uuid" && !(Guid.TryParseExact(value.GetString(), "D", out _)))
            yield return $"{path} is '{value.GetString()}' but the schema format is uuid";
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.Properties;
            foreach (var required in schema.Required.Where(r => !value.TryGetProperty(r, out _)))
                yield return $"{path}.{required} is required by the schema but absent from the body";
            foreach (var property in value.EnumerateObject())
            {
                if (properties.TryGetValue(property.Name, out var child))
                    foreach (var violation in Validate(property.Value, child, $"{path}.{property.Name}", allowExtra)) yield return violation;
                else if (!allowExtra && !schema.HasUndeclaredProperties)
                    yield return $"{path}.{property.Name} is in the body but not documented in the schema";
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.Items is { } items)
            foreach (var (item, index) in value.EnumerateArray().Select((item, index) => (item, index)))
                foreach (var violation in Validate(item, items, $"{path}[{index}]", allowExtra)) yield return violation;
    }

    private static bool Matches(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && (value.TryGetInt64(out _) || value.GetDouble() % 1 == 0),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        _ => true
    };

    private static string Describe(JsonElement value) => value.ValueKind.ToString().ToLowerInvariant();
}
