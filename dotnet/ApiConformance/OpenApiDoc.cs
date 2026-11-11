using System.Text.RegularExpressions;
using System.Text.Json;

namespace ApiConformance;

public sealed class OpenApiSchema(JsonElement raw, OpenApiDoc doc)
{
    private readonly JsonElement node = doc.Resolve(raw);

    public string? Ref { get; } = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("$ref", out var reference) ? reference.GetString() : null;

    public IReadOnlyList<string> Types => node.TryGetProperty("type", out var type)
        ? type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToList() : [type.GetString()!]
        : [];

    public IReadOnlyList<string> NonNullTypes => Types.Where(t => t != "null").ToList();

    public string? Format => node.TryGetProperty("format", out var format) ? format.GetString() : null;

    public bool IsString => NonNullTypes.Contains("string");

    public bool IsUuid => IsString && Format == "uuid";

    public IReadOnlyDictionary<string, OpenApiSchema> Properties
    {
        get
        {
            var all = new Dictionary<string, OpenApiSchema>();
            foreach (var part in Parts())
                if (part.TryGetProperty("properties", out var properties))
                    foreach (var property in properties.EnumerateObject()) all[property.Name] = new OpenApiSchema(property.Value, doc);
            return all;
        }
    }

    public IReadOnlySet<string> Required => Parts()
        .Where(part => part.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        .SelectMany(part => part.GetProperty("required").EnumerateArray().Select(r => r.GetString()!))
        .ToHashSet();

    public OpenApiSchema? Items => node.TryGetProperty("items", out var items) ? new OpenApiSchema(items, doc) : null;

    public bool HasUndeclaredProperties => node.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind != JsonValueKind.False;

    public IEnumerable<OpenApiSchema> Children() =>
        Properties.Values.Concat(Items is { } items ? [items] : []).Concat(Alternatives().Select(a => new OpenApiSchema(a, doc)));

    private IEnumerable<JsonElement> Alternatives() => new[] { "allOf", "oneOf", "anyOf" }
        .Where(key => node.TryGetProperty(key, out _))
        .SelectMany(key => node.GetProperty(key).EnumerateArray());

    private IEnumerable<JsonElement> Parts() => [node, .. Alternatives().Select(doc.Resolve)];

}

public sealed record OpenApiParameter(string Name, string In, bool Required, OpenApiSchema? Schema);

public sealed record OpenApiBody(bool Required, IReadOnlyDictionary<string, OpenApiSchema?> Content);

public sealed record OpenApiResponse(string Status, IReadOnlyDictionary<string, OpenApiSchema?> Content);

public sealed record OpenApiOperation(string Method, string Path, string? OperationId, IReadOnlyList<OpenApiParameter> Parameters, OpenApiBody? RequestBody, IReadOnlyList<OpenApiResponse> Responses)
{
    public string Key => $"{Method} {Path}";

    public bool Declares(int status) => Responses.Any(r => r.Status == status.ToString());

    public OpenApiResponse? Response(int status) => Responses.FirstOrDefault(r => r.Status == status.ToString())
        ?? Responses.FirstOrDefault(r => r.Status == $"{status / 100}XX") ?? Responses.FirstOrDefault(r => r.Status == "default");

    public OpenApiParameter? Header(string name) => Parameters.FirstOrDefault(p => p.In == "header" && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed partial class OpenApiDoc
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];
    private readonly JsonElement root;

    private OpenApiDoc(JsonElement root, string json)
    {
        this.root = root;
        Json = json;
    }

    public string Json { get; }

    public static OpenApiDoc Parse(string json) => new(JsonDocument.Parse(json).RootElement.Clone(), json);

    public JsonElement Resolve(JsonElement element)
    {
        for (var depth = 0; depth < 32 && element.ValueKind == JsonValueKind.Object && element.TryGetProperty("$ref", out var reference); depth++)
        {
            var target = root;
            foreach (var segment in reference.GetString()!.TrimStart('#', '/').Split('/')) target = target.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
            element = target;
        }
        return element;
    }

    public IReadOnlyDictionary<string, OpenApiSchema> ComponentSchemas =>
        root.TryGetProperty("components", out var components) && components.TryGetProperty("schemas", out var schemas)
            ? schemas.EnumerateObject().ToDictionary(s => s.Name, s => new OpenApiSchema(s.Value, this))
            : new Dictionary<string, OpenApiSchema>();

    public IReadOnlyList<OpenApiOperation> Operations =>
        !root.TryGetProperty("paths", out var paths) ? [] :
        [.. from path in paths.EnumerateObject()
            from method in path.Value.EnumerateObject()
            where Methods.Contains(method.Name)
            select ReadOperation(path.Name, method.Name.ToUpperInvariant(), method.Value, path.Value)];

    public IEnumerable<OpenApiSchema> AllSchemas()
    {
        var seen = new HashSet<string>();
        var queue = new Queue<OpenApiSchema>(ComponentSchemas.Values.Concat(Operations.SelectMany(OperationSchemas)));
        while (queue.Count > 0)
        {
            var schema = queue.Dequeue();
            if (schema.Ref is not null && !seen.Add(schema.Ref)) continue;
            yield return schema;
            foreach (var child in schema.Children()) queue.Enqueue(child);
        }
    }

    public static IEnumerable<OpenApiSchema> OperationSchemas(OpenApiOperation operation) =>
        operation.Parameters.Select(p => p.Schema).Concat(operation.RequestBody?.Content.Values ?? [])
            .Concat(operation.Responses.SelectMany(r => r.Content.Values)).OfType<OpenApiSchema>();

    public OpenApiOperation? Match(string method, string uri)
    {
        var path = new Uri(uri, UriKind.RelativeOrAbsolute) is { IsAbsoluteUri: true } absolute ? absolute.AbsolutePath : uri.Split('?')[0];
        return Operations
            .Where(o => string.Equals(o.Method, method, StringComparison.OrdinalIgnoreCase) && TemplateRegex(o.Path).IsMatch(path))
            .OrderByDescending(o => o.Path.Count(c => c != '{'))
            .FirstOrDefault();
    }

    public static string Normalize(string template) => PlaceholderRegex().Replace(template, "{}");

    private static Regex TemplateRegex(string template) => new("^" + string.Join("[^/]+", PlaceholderRegex().Split(template).Select(Regex.Escape)) + "/?$");

    [GeneratedRegex(@"\{[^}/]+\}")]
    private static partial Regex PlaceholderRegex();

    private OpenApiOperation ReadOperation(string path, string method, JsonElement operation, JsonElement pathItem)
    {
        var parameters = (pathItem.TryGetProperty("parameters", out var shared) ? shared.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Concat(operation.TryGetProperty("parameters", out var own) ? own.EnumerateArray() : [])
            .Select(Resolve)
            .Select(p => new OpenApiParameter(p.GetProperty("name").GetString()!, p.GetProperty("in").GetString()!,
                p.TryGetProperty("required", out var required) && required.GetBoolean(),
                p.TryGetProperty("schema", out var schema) ? new OpenApiSchema(schema, this) : null))
            .ToList();
        OpenApiBody? body = operation.TryGetProperty("requestBody", out var requestBody)
            ? new(Resolve(requestBody) is var resolved && resolved.TryGetProperty("required", out var bodyRequired) && bodyRequired.GetBoolean(), ReadContent(Resolve(requestBody)))
            : null;
        var responses = operation.TryGetProperty("responses", out var all)
            ? all.EnumerateObject().Select(r => new OpenApiResponse(r.Name, ReadContent(Resolve(r.Value)))).ToList()
            : [];
        return new OpenApiOperation(method, path, operation.TryGetProperty("operationId", out var id) ? id.GetString() : null, parameters, body, responses);
    }

    private IReadOnlyDictionary<string, OpenApiSchema?> ReadContent(JsonElement holder) =>
        holder.TryGetProperty("content", out var content)
            ? content.EnumerateObject().ToDictionary(c => c.Name, c => c.Value.TryGetProperty("schema", out var schema) ? new OpenApiSchema(schema, this) : null)
            : new Dictionary<string, OpenApiSchema?>();
}
