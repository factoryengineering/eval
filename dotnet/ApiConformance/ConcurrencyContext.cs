using System.Text.Json;

namespace ApiConformance;

public sealed record Call(HttpMethod Method, string Uri, string? Json = null, IDictionary<string, string>? Headers = null);

public sealed class ResponseLostException(int status) : Exception($"The response (status {status}) was lost after the server processed the request.")
{
    public int Status { get; } = status;
}

public sealed class LostResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await base.SendAsync(request, cancellationToken);
        await response.Content.ReadAsByteArrayAsync(cancellationToken);
        throw new ResponseLostException((int)response.StatusCode);
    }
}

public sealed partial class ScenarioContext
{
    public static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(20);

    public async Task<HttpTrace[]> Race(params Call[] calls)
    {
        var traces = await Host.Interleaver.Run<HttpTrace>([.. calls.Select<Call, Func<string, Task<HttpTrace>>>(call => id =>
            HttpTrace.Send(Host.Client, call.Method, call.Uri, call.Json, new Dictionary<string, string>(call.Headers ?? new Dictionary<string, string>()) { [Interleaver.ParticipantHeader] = id }))], RaceTimeout);
        for (var index = 0; index < traces.Length; index++) Record($"Participant {index}", traces[index]);
        transcript.AppendLine(Host.Interleaver.Describe());
        RequireNoServerError(traces);
        return traces;
    }

    public async Task<HttpTrace> SendAndLoseResponse(Call call)
    {
        using var client = new HttpClient(new LostResponseHandler(Host.CreateHandler())) { BaseAddress = Host.Client.BaseAddress };
        try
        {
            await HttpTrace.Send(client, call.Method, call.Uri, call.Json, call.Headers);
        }
        catch (ResponseLostException lost)
        {
            Record($"Response lost ({call.Method} {call.Uri})", lost.Status);
            Require(lost.Status is >= 200 and < 300, $"The first delivery of {call.Method} {call.Uri} should succeed before its response is lost, got {lost.Status}.");
            return new HttpTrace(call.Method, call.Uri, call.Headers?.ToDictionary(h => h.Key, h => new[] { h.Value }) ?? [], call.Json, (System.Net.HttpStatusCode)lost.Status, new Dictionary<string, string[]>(), "");
        }
        throw new ScenarioFailure("The lossy client should have lost the response.");
    }

    public Task<HttpTrace> Send(Call call) => Send(call.Method, call.Uri, call.Json, call.Headers);

    public Call Post(Guid? parentId, Guid id, IReadOnlyList<JsonElement> values) => new(HttpMethod.Post, CreateRoute(parentId), Body(id, parentId, values));

    public Call PutCall(Guid id, Guid? parentId, IReadOnlyList<JsonElement> values, string ifMatch) =>
        new(HttpMethod.Put, ItemRoute(id), Body(id, parentId, values), Headers(ifMatch));

    public Call DeleteCall(Guid id, string ifMatch) => new(HttpMethod.Delete, ItemRoute(id), null, Headers(ifMatch));

    public void RequireNoServerError(IEnumerable<HttpTrace> traces) =>
        Require(traces.All(t => t.StatusCode < 500), $"No response may be a 5xx under concurrency, got [{string.Join(", ", traces.Select(t => t.StatusCode))}].");

    public void RequireStatuses(IEnumerable<HttpTrace> traces, int[] expected, string what)
    {
        var actual = traces.Select(t => t.StatusCode).Order().ToArray();
        Require(actual.SequenceEqual(expected.Order()), $"{what}: expected statuses [{string.Join(", ", expected.Order())}] in some order but got [{string.Join(", ", actual)}].");
    }

    public async Task RequireSingleResource(Guid id, string what)
    {
        var rows = await Host.CountRowsHoldingGuid(id);
        Require(rows == 1, $"{what}: exactly one stored row should hold id {id}, found {rows}.");
    }

    public async Task RequireFinalState(Guid id, Guid? parentId, IReadOnlyList<JsonElement> values, string what)
    {
        var read = await Get(id);
        RequireStatus(read, 200, $"{what}: GET of the final state");
        RequireRepresentation(read, id, parentId, values, $"{what}: final state");
        Require(read.Header("ETag") == Quoted(Hash(id, parentId, values)), $"{what}: the final ETag must be the hash of the final representation.");
    }

    private void Record(string label, HttpTrace trace) => transcript.AppendLine($"[{label}]").AppendLine(trace.ToString());

    private void Record(string label, int status) => transcript.AppendLine($"[{label}] server answered {status}");
}
