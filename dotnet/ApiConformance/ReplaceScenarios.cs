namespace ApiConformance;

public static partial class ProtocolScenarios
{
    public const string ReplaceGroup = "replace";

    public static IReadOnlyList<Scenario> Replace(DomainDescriptor domain)
    {
        var scenarios = new List<Scenario>
        {
            new("replace-without-if-match-is-precondition-required", ReplaceGroup, ReplaceWithoutIfMatchIsPreconditionRequired),
            new("replace-without-if-match-on-unknown-id-is-precondition-required", ReplaceGroup, ReplaceWithoutIfMatchOnUnknownIdIsPreconditionRequired),
            new("replace-invalid-body-is-bad-request", ReplaceGroup, ReplaceInvalidBodyIsBadRequest),
            new("replace-body-id-differs-from-route-is-bad-request", ReplaceGroup, ReplaceBodyIdDiffersFromRouteIsBadRequest),
            new("replace-malformed-json-without-if-match-is-bad-request", ReplaceGroup, ReplaceMalformedJsonWithoutIfMatchIsBadRequest),
            new("replace-body-id-differs-from-route-without-if-match-is-bad-request", ReplaceGroup, ReplaceBodyIdDiffersWithoutIfMatchIsBadRequest),
            new("replace-unknown-id-with-invalid-body-without-if-match-is-bad-request", ReplaceGroup, ReplaceUnknownIdInvalidBodyWithoutIfMatchIsBadRequest),
            new("replace-with-current-if-match-updates", ReplaceGroup, ReplaceWithCurrentIfMatchUpdates),
            new("replace-retry-with-stale-if-match-same-body-succeeds", ReplaceGroup, ReplaceRetryWithStaleIfMatchSucceeds),
            new("replace-identical-body-with-stale-if-match-succeeds", ReplaceGroup, ReplaceIdenticalBodyWithStaleIfMatchSucceeds),
            new("replace-stale-if-match-different-body-is-precondition-failed", ReplaceGroup, ReplaceStaleIfMatchDifferentBodyIsPreconditionFailed),
            new("replace-with-wildcard-if-match-updates", ReplaceGroup, ReplaceWithWildcardIfMatchUpdates),
            new("replace-unknown-id-is-not-found", ReplaceGroup, ReplaceUnknownIdIsNotFound),
            new("replace-tombstoned-is-gone", ReplaceGroup, ReplaceTombstonedIsGone)
        };
        if (domain.Parent is not null)
        {
            scenarios.Add(new("child-replace-different-parent-conflicts", ReplaceGroup, ChildReplaceDifferentParentConflicts));
            scenarios.Add(new("child-replace-different-parent-without-if-match-is-precondition-required", ReplaceGroup, ChildReplaceDifferentParentWithoutIfMatchIsPreconditionRequired));
        }
        return scenarios;
    }

    private static async Task ReplaceWithoutIfMatchIsPreconditionRequired(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var put = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, context.Values(1)), null);

        context.RequireStatus(put, 428, "PUT without If-Match");
        var read = await context.Get(resource.Id);
        context.RequireRepresentation(read, resource.Id, resource.ParentId, resource.Values, "Resource after the rejected PUT");
    }

    private static async Task ReplaceWithoutIfMatchOnUnknownIdIsPreconditionRequired(ScenarioContext context)
    {
        var id = Guid.NewGuid();
        var parentId = context.Domain.Parent is null ? (Guid?)null : Guid.NewGuid();

        var put = await context.Put(id, context.Body(id, parentId, context.Values(1)), null);

        context.RequireStatus(put, 428, "PUT without If-Match on an unknown id");
    }

    private static async Task ReplaceInvalidBodyIsBadRequest(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var omitted = context.Domain.WritableFields[0].Name;

        var put = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, context.Values(1), omitted), null);

        context.RequireStatus(put, 400, $"PUT without writable field '{omitted}' and without If-Match");
    }

    private static async Task ReplaceBodyIdDiffersFromRouteIsBadRequest(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var put = await context.Put(resource.Id, context.Body(Guid.NewGuid(), resource.ParentId, context.Values(1)), resource.ETag);

        context.RequireStatus(put, 400, "PUT with a body id differing from the route");
    }

    private static async Task ReplaceMalformedJsonWithoutIfMatchIsBadRequest(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var put = await context.Put(resource.Id, "{ not json", null);

        context.RequireStatus(put, 400, "PUT with a malformed JSON body and without If-Match");
    }

    private static async Task ReplaceBodyIdDiffersWithoutIfMatchIsBadRequest(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var put = await context.Put(resource.Id, context.Body(Guid.NewGuid(), resource.ParentId, context.Values(1)), null);

        context.RequireStatus(put, 400, "PUT with a body id differing from the route and without If-Match");
    }

    private static async Task ReplaceUnknownIdInvalidBodyWithoutIfMatchIsBadRequest(ScenarioContext context)
    {
        var id = Guid.NewGuid();
        var parentId = context.Domain.Parent is null ? (Guid?)null : Guid.NewGuid();

        var put = await context.Put(id, context.Body(Guid.NewGuid(), parentId, context.Values(1)), null);

        context.RequireStatus(put, 400, "PUT of an unknown id with a mismatching body id and without If-Match");
    }

    private static async Task ReplaceWithCurrentIfMatchUpdates(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var changed = context.Values(1);

        var put = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, changed), resource.ETag);

        context.RequireStatus(put, 204, "PUT with the current If-Match");
        var expected = ScenarioContext.Quoted(context.Hash(resource.Id, resource.ParentId, changed));
        context.Require(put.Header("ETag") == expected, $"PUT must return the canonical hash {expected} of the new representation, got {put.Header("ETag")}.");
        var read = await context.Get(resource.Id);
        context.RequireStatus(read, 200, "GET after PUT");
        context.RequireRepresentation(read, resource.Id, resource.ParentId, changed, "GET body after PUT");
        context.Require(read.Header("ETag") == expected, "GET after PUT must return the new ETag.");
    }

    private static async Task ReplaceRetryWithStaleIfMatchSucceeds(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var body = context.Body(resource.Id, resource.ParentId, context.Values(1));
        var first = await context.Put(resource.Id, body, resource.ETag);
        context.RequireStatus(first, 204, "First PUT");

        var retry = await context.Put(resource.Id, body, resource.ETag);

        context.RequireStatus(retry, 204, "Retried PUT with the now-stale If-Match");
        context.Require(retry.Header("ETag") == first.Header("ETag"), "The retried PUT must return the same ETag.");
    }

    private static async Task ReplaceIdenticalBodyWithStaleIfMatchSucceeds(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var put = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, resource.Values), ScenarioContext.StaleETag);

        context.RequireStatus(put, 204, "No-op PUT with a stale If-Match");
        context.Require(put.Header("ETag") == resource.ETag, "The no-op PUT must return the unchanged ETag.");
    }

    private static async Task ReplaceStaleIfMatchDifferentBodyIsPreconditionFailed(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var first = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, context.Values(1)), resource.ETag);
        context.RequireStatus(first, 204, "First PUT");

        var conflict = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, context.Values(2)), resource.ETag);

        context.RequireStatus(conflict, 412, "PUT with a stale If-Match and a different body");
        context.Require(conflict.Header("ETag") == first.Header("ETag"), $"The 412 must carry the current ETag {first.Header("ETag")}, got {conflict.Header("ETag") ?? "none"}.");
        var read = await context.Get(resource.Id);
        context.RequireRepresentation(read, resource.Id, resource.ParentId, context.Values(1), "Resource after the rejected PUT");
    }

    private static async Task ReplaceWithWildcardIfMatchUpdates(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var changed = context.Values(1);

        var put = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, changed), "*");

        context.RequireStatus(put, 204, "PUT with If-Match *");
        var read = await context.Get(resource.Id);
        context.RequireRepresentation(read, resource.Id, resource.ParentId, changed, "GET body after PUT");
    }

    private static async Task ReplaceUnknownIdIsNotFound(ScenarioContext context)
    {
        var id = Guid.NewGuid();
        var parentId = context.Domain.Parent is null ? (Guid?)null : Guid.NewGuid();

        var put = await context.Put(id, context.Body(id, parentId, context.Values(1)), "*");

        context.RequireStatus(put, 404, "PUT of an unknown id");
    }

    private static async Task ReplaceTombstonedIsGone(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var deleted = await context.Delete(context.ItemRoute(resource.Id), resource.ETag);
        context.RequireStatus(deleted, 204, "DELETE with the current ETag");

        var put = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, context.Values(1)), resource.ETag);

        context.RequireStatus(put, 410, "PUT of a tombstoned resource");
    }

    private static async Task ChildReplaceDifferentParentConflicts(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var otherParentId = await context.CreateParent();

        var put = await context.Put(resource.Id, context.Body(resource.Id, otherParentId, context.Values(1)), resource.ETag);

        context.RequireStatus(put, 409, "PUT with a different parent id than stored");
    }

    private static async Task ChildReplaceDifferentParentWithoutIfMatchIsPreconditionRequired(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var otherParentId = await context.CreateParent();

        var put = await context.Put(resource.Id, context.Body(resource.Id, otherParentId, context.Values(1)), null);

        context.RequireStatus(put, 428, "PUT with a different parent id and without If-Match");
    }
}
