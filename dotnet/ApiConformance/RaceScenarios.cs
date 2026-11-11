namespace ApiConformance;

public static partial class ProtocolScenarios
{
    public const string RaceGroup = "race";

    public static IReadOnlyList<Scenario> Race(DomainDescriptor domain)
    {
        var scenarios = new List<Scenario>
        {
            new("race-create-same-id-same-body-both-succeed", RaceGroup, RaceCreateSameIdSameBody),
            new("race-create-same-id-different-body-one-conflicts", RaceGroup, RaceCreateSameIdDifferentBody),
            new("race-put-same-body-same-etag-both-succeed", RaceGroup, RacePutSameBody),
            new("race-put-different-body-same-etag-one-precondition-failed", RaceGroup, RacePutDifferentBody),
            new("race-delete-same-if-match-both-succeed", RaceGroup, RaceDeleteSameIfMatch),
            new("race-put-then-delete-same-etag-is-serializable", RaceGroup, context => RacePutAndDelete(context, putFirst: true)),
            new("race-delete-then-put-same-etag-is-serializable", RaceGroup, context => RacePutAndDelete(context, putFirst: false))
        };
        if (domain.Parent is not null)
        {
            scenarios.Add(new("race-child-create-same-id-different-parent-one-conflicts", RaceGroup, RaceChildCreateSameIdDifferentParent));
            scenarios.Add(new("race-parent-delete-same-if-match-tombstones-children-once", RaceGroup, RaceParentDeleteSameIfMatch));
        }
        return scenarios;
    }

    private static async Task RaceCreateSameIdSameBody(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var values = context.Values(0);
        var create = context.Post(parentId, id, values);

        var traces = await context.Race(create, create);

        foreach (var trace in traces)
        {
            context.RequireSuccess(trace, "Concurrent identical create");
            context.Require(trace.Header("ETag") == ScenarioContext.Quoted(context.Hash(id, parentId, values)), "Both identical creates must carry the canonical ETag.");
        }
        await context.RequireSingleResource(id, "After two identical concurrent creates");
        await context.RequireFinalState(id, parentId, values, "After two identical concurrent creates");
    }

    private static async Task RaceCreateSameIdDifferentBody(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var bodies = new[] { context.Values(0), context.Values(1) };

        var traces = await context.Race(context.Post(parentId, id, bodies[0]), context.Post(parentId, id, bodies[1]));

        context.RequireStatuses(traces, [201, 409], "Concurrent creates with the same id and different bodies");
        await context.RequireSingleResource(id, "After two conflicting concurrent creates");
        await context.RequireFinalState(id, parentId, bodies[Array.FindIndex(traces, t => t.StatusCode == 201)], "After two conflicting concurrent creates");
    }

    private static async Task RacePutSameBody(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var changed = context.Values(1);
        var put = context.PutCall(resource.Id, resource.ParentId, changed, resource.ETag);

        var traces = await context.Race(put, put);

        var expected = ScenarioContext.Quoted(context.Hash(resource.Id, resource.ParentId, changed));
        foreach (var trace in traces)
        {
            context.RequireStatus(trace, 204, "Concurrent identical PUT");
            context.Require(trace.Header("ETag") == expected, "Both identical PUTs must carry the ETag of the new representation.");
        }
        await context.RequireFinalState(resource.Id, resource.ParentId, changed, "After two identical concurrent PUTs");
    }

    private static async Task RacePutDifferentBody(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var bodies = new[] { context.Values(1), context.Values(2) };

        var traces = await context.Race(
            context.PutCall(resource.Id, resource.ParentId, bodies[0], resource.ETag),
            context.PutCall(resource.Id, resource.ParentId, bodies[1], resource.ETag));

        context.RequireStatuses(traces, [204, 412], "Concurrent PUTs with different bodies from the same ETag");
        var winner = Array.FindIndex(traces, t => t.StatusCode == 204);
        var loser = traces[1 - winner];
        var winnerETag = ScenarioContext.Quoted(context.Hash(resource.Id, resource.ParentId, bodies[winner]));
        context.Require(loser.Header("ETag") == winnerETag, $"The 412 must carry the current ETag {winnerETag}, got {loser.Header("ETag") ?? "none"}.");
        await context.RequireFinalState(resource.Id, resource.ParentId, bodies[winner], "After two conflicting concurrent PUTs");
    }

    private static async Task RaceDeleteSameIfMatch(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var delete = context.DeleteCall(resource.Id, resource.ETag);

        var traces = await context.Race(delete, delete);

        context.RequireStatuses(traces, [204, 204], "Concurrent DELETEs with the same If-Match");
        context.RequireStatus(await context.Get(resource.Id), 410, "GET after two concurrent DELETEs");
    }

    private static async Task RacePutAndDelete(ScenarioContext context, bool putFirst)
    {
        var resource = await context.CreateResource();
        var changed = context.Values(1);
        var put = context.PutCall(resource.Id, resource.ParentId, changed, resource.ETag);
        var delete = context.DeleteCall(resource.Id, resource.ETag);

        var traces = await context.Race(putFirst ? [put, delete] : [delete, put]);

        var putTrace = traces[putFirst ? 0 : 1];
        var deleteTrace = traces[putFirst ? 1 : 0];
        var read = await context.Get(resource.Id);
        var putThenDelete = putTrace.StatusCode == 204 && deleteTrace.StatusCode == 412;
        var deleteThenPut = deleteTrace.StatusCode == 204 && putTrace.StatusCode == 410;
        context.Require(putThenDelete || deleteThenPut,
            $"PUT and DELETE from the same ETag must match a serial order: PUT 204 then DELETE 412, or DELETE 204 then PUT 410. Got PUT {putTrace.StatusCode} and DELETE {deleteTrace.StatusCode}.");
        if (putThenDelete)
        {
            context.RequireStatus(read, 200, "GET after PUT-then-DELETE");
            await context.RequireFinalState(resource.Id, resource.ParentId, changed, "After PUT then rejected DELETE");
        }
        else
            context.RequireStatus(read, 410, "GET after DELETE-then-PUT");
    }

    private static async Task RaceChildCreateSameIdDifferentParent(ScenarioContext context)
    {
        var parents = new[] { await context.CreateParent(), await context.CreateParent() };
        var id = Guid.NewGuid();
        var values = context.Values(0);

        var traces = await context.Race(context.Post(parents[0], id, values), context.Post(parents[1], id, values));

        context.RequireStatuses(traces, [201, 409], "Concurrent child creates with the same id under different parents");
        await context.RequireSingleResource(id, "After two concurrent child creates under different parents");
        await context.RequireFinalState(id, parents[Array.FindIndex(traces, t => t.StatusCode == 201)], values, "After two concurrent child creates");
    }

    private static async Task RaceParentDeleteSameIfMatch(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var parentRoute = $"{context.Domain.Parent!.CollectionRoute}/{resource.ParentId}";
        var delete = new Call(HttpMethod.Delete, parentRoute, null, ScenarioContext.Headers(await context.ParentETag(resource.ParentId!.Value)));

        var traces = await context.Race(delete, delete);

        context.RequireStatuses(traces, [204, 204], "Concurrent DELETEs of a parent with the same If-Match");
        context.RequireStatus(await context.Get(resource.Id), 410, "GET of a child of the concurrently deleted parent");
    }
}
