using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiConformance;

public sealed record FieldDescriptor(string Name, string JsonType, string ClrType, IReadOnlyList<JsonElement> Samples);

public sealed record ParentDescriptor(string CollectionRoute, string ChildCollectionRoute, string ParentBodyField);

public sealed record DomainDescriptor(
    string Name,
    string CollectionRoute,
    ParentDescriptor? Parent,
    IReadOnlyList<FieldDescriptor> WritableFields,
    IReadOnlyList<string> ServerOwnedFields)
{
    public const int MinimumSamples = 3;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static DomainDescriptor Parse(string json)
    {
        var domain = JsonSerializer.Deserialize<DomainDescriptor>(json, Json)
            ?? throw new JsonException("Domain descriptor is empty.");
        domain.Validate();
        return domain;
    }

    public static DomainDescriptor Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(CollectionRoute)) throw new JsonException($"Domain '{Name}' has no collection route.");
        if (WritableFields.Count == 0) throw new JsonException($"Domain '{Name}' has no writable fields.");
        foreach (var field in WritableFields)
        {
            var distinct = field.Samples.Select(s => s.GetRawText()).Distinct().Count();
            if (distinct < MinimumSamples)
                throw new JsonException($"Field '{field.Name}' of domain '{Name}' has {distinct} distinct samples; at least {MinimumSamples} are required.");
        }
    }
}
