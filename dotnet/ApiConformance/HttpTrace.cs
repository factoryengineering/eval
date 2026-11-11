using System.Net;
using System.Text;

namespace ApiConformance;

public sealed record HttpTrace(
    HttpMethod Method,
    string Uri,
    IReadOnlyDictionary<string, string[]> RequestHeaders,
    string? RequestBody,
    HttpStatusCode Status,
    IReadOnlyDictionary<string, string[]> ResponseHeaders,
    string ResponseBody)
{
    public int StatusCode => (int)Status;

    public string? Header(string name) =>
        ResponseHeaders.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value is { } values
            ? string.Join(", ", values)
            : null;

    public static async Task<HttpTrace> Send(HttpClient client, HttpRequestMessage request)
    {
        var requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
        var requestHeaders = Flatten(request.Headers, request.Content?.Headers);
        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        return new HttpTrace(request.Method, request.RequestUri?.ToString() ?? "", requestHeaders, requestBody,
            response.StatusCode, Flatten(response.Headers, response.Content.Headers), responseBody);
    }

    public static Task<HttpTrace> Send(HttpClient client, HttpMethod method, string uri, string? json = null, IDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);
        return Send(client, request);
    }

    public override string ToString()
    {
        var text = new StringBuilder();
        text.AppendLine($"{Method} {Uri}");
        foreach (var (name, values) in RequestHeaders) text.AppendLine($"  {name}: {string.Join(", ", values)}");
        if (RequestBody is not null) text.AppendLine($"  body: {RequestBody}");
        text.AppendLine($"-> {StatusCode} {Status}");
        foreach (var (name, values) in ResponseHeaders) text.AppendLine($"  {name}: {string.Join(", ", values)}");
        if (ResponseBody.Length > 0) text.AppendLine($"  body: {ResponseBody}");
        return text.ToString();
    }

    private static Dictionary<string, string[]> Flatten(params System.Net.Http.Headers.HttpHeaders?[] sets) =>
        sets.Where(s => s is not null).SelectMany(s => s!).ToDictionary(h => h.Key, h => h.Value.ToArray());
}
