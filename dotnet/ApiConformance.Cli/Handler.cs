using System.Text.Json;
using ApiConformance;

namespace ApiConformance.Cli;

public sealed record Request(string? Id, JsonElement Domain, List<string> Sources);

public sealed record CompileInfo(bool Ok, IReadOnlyList<string> Errors);

public sealed record Response(string? Id, CompileInfo Compile, IReadOnlyList<CheckEntry> Checks, string? Error = null);

public static class Handler
{
    public static async Task<Response> Handle(string line, JsonSerializerOptions json)
    {
        string? id = null;
        try
        {
            var request = JsonSerializer.Deserialize<Request>(line, json) ?? throw new JsonException("Empty request.");
            id = request.Id;
            var domain = request.Domain.ValueKind == JsonValueKind.String
                ? DomainDescriptor.Load(request.Domain.GetString()!)
                : DomainDescriptor.Parse(request.Domain.GetRawText());
            var report = await CheckReport.Run(request.Sources, domain);
            return new(id, new(report.Compiled, report.CompileErrors), report.Checks);
        }
        catch (Exception failure)
        {
            return new(id, new(false, []), [], $"{failure.GetType().Name}: {failure.Message}");
        }
    }
}
