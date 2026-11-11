using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ApiConformance;

public static class CanonicalHash
{
    public static string Json(IEnumerable<KeyValuePair<string, object>> fields)
    {
        var json = new StringBuilder("{");
        var first = true;
        foreach (var (name, value) in fields)
        {
            if (!first) json.Append(',');
            first = false;
            AppendString(json, name);
            json.Append(':');
            AppendValue(json, value);
        }
        return json.Append('}').ToString();
    }

    public static string Compute(IEnumerable<KeyValuePair<string, object>> fields) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Json(fields)))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Compute(DomainDescriptor domain, Guid id, Guid? parentId, IReadOnlyList<JsonElement> values) =>
        Compute(Fields(domain, id, parentId, values));

    public static IEnumerable<KeyValuePair<string, object>> Fields(DomainDescriptor domain, Guid id, Guid? parentId, IReadOnlyList<JsonElement> values)
    {
        yield return new("id", id);
        if (domain.Parent is { } parent && parentId is { } parentValue) yield return new(parent.ParentBodyField, parentValue);
        for (var index = 0; index < domain.WritableFields.Count; index++)
            yield return new(domain.WritableFields[index].Name, values[index]);
    }

    private static void AppendValue(StringBuilder json, object value)
    {
        switch (value)
        {
            case Guid guid: AppendString(json, guid.ToString("D")); break;
            case string text: AppendString(json, text); break;
            case bool flag: json.Append(flag ? "true" : "false"); break;
            case int or long: json.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            case JsonElement { ValueKind: JsonValueKind.String } element: AppendString(json, element.GetString()!); break;
            case JsonElement element: json.Append(element.GetRawText()); break;
            default: throw new ArgumentException($"Unsupported value type {value.GetType()}.");
        }
    }

    private static void AppendString(StringBuilder json, string text)
    {
        json.Append('"');
        foreach (var character in text.Normalize(NormalizationForm.FormC))
            switch (character)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                case < ' ': json.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: json.Append(character); break;
            }
        json.Append('"');
    }
}
