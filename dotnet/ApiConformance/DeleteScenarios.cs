namespace ApiConformance;

public static partial class ProtocolScenarios
{
    public const string DeleteGroup = "delete";

    public static IReadOnlyList<Scenario> Delete(DomainDescriptor domain)
    {
        var scenarios = new List<Scenario>
        {
            new("delete-without-if-match-is-precondition-required", DeleteGroup, DeleteWithoutIfMatchIsPreconditionRequired),
            new("delete-with-current-if-match-tombstones", DeleteGroup, DeleteWithCurrentIfMatchTombstones),
            new("delete-retry-is-no-content", DeleteGroup, DeleteRetryIsNoContent),
            new("delete-unknown-id-is-no-content", DeleteGroup, DeleteUnknownIdIsNoContent),
            new("delete-stale-if-match-is-precondition-failed", DeleteGroup, DeleteStaleIfMatchIsPreconditionFailed),
            new("create-replay-of-tombstoned-is-gone", DeleteGroup, CreateReplayOfTombstonedIsGone)
        };
        if (domain.Parent is not null)
        {
            scenarios.Add(new("child-delete-parent-tombstones-children", DeleteGroup, ChildDeleteParentTombstonesChildren));
            scenarios.Add(new("child-create-under-tombstoned-parent-is-gone", DeleteGroup, ChildCreateUnderTombstonedParentIsGone));
        }
        return scenarios;
    }

    private static async Task DeleteWithoutIfMatchIsPreconditionRequired(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var deleted = await context.Delete(context.ItemRoute(resource.Id), null);

        context.RequireStatus(deleted, 428, "DELETE without If-Match");
        context.RequireStatus(await context.Get(resource.Id), 200, "GET after the rejected DELETE");
    }

    private static async Task DeleteWithCurrentIfMatchTombstones(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var deleted = await context.Delete(context.ItemRoute(resource.Id), resource.ETag);

        context.RequireStatus(deleted, 204, "DELETE with the current If-Match");
        context.RequireStatus(await context.Get(resource.Id), 410, "GET after DELETE");
    }

    private static async Task DeleteRetryIsNoContent(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var first = await context.Delete(context.ItemRoute(resource.Id), resource.ETag);
        context.RequireStatus(first, 204, "First DELETE");

        var retry = await context.Delete(context.ItemRoute(resource.Id), resource.ETag);

        context.RequireStatus(retry, 204, "Retried DELETE");
    }

    private static async Task DeleteUnknownIdIsNoContent(ScenarioContext context)
    {
        var deleted = await context.Delete(context.ItemRoute(Guid.NewGuid()), ScenarioContext.StaleETag);

        context.RequireStatus(deleted, 204, "DELETE of an unknown id");
    }

    private static async Task DeleteStaleIfMatchIsPreconditionFailed(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var deleted = await context.Delete(context.ItemRoute(resource.Id), ScenarioContext.StaleETag);

        context.RequireStatus(deleted, 412, "DELETE with a stale If-Match on a live resource");
        context.RequireStatus(await context.Get(resource.Id), 200, "GET after the rejected DELETE");
    }

    private static async Task CreateReplayOfTombstonedIsGone(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var deleted = await context.Delete(context.ItemRoute(resource.Id), resource.ETag);
        context.RequireStatus(deleted, 204, "DELETE with the current ETag");

        var replay = await context.Send(HttpMethod.Post, context.CreateRoute(resource.ParentId), resource.Created.RequestBody);

        context.RequireStatus(replay, 410, "Replay of the create of a tombstoned resource");
    }

    private static async Task ChildDeleteParentTombstonesChildren(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var parentRoute = $"{context.Domain.Parent!.CollectionRoute}/{resource.ParentId}";

        var deleted = await context.Delete(parentRoute, await context.ParentETag(resource.ParentId!.Value));

        context.RequireStatus(deleted, 204, "DELETE of the parent");
        context.RequireStatus(await context.Get(resource.Id), 410, "GET of a child of the deleted parent");
    }

    private static async Task ChildCreateUnderTombstonedParentIsGone(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var deleted = await context.Delete($"{context.Domain.Parent!.CollectionRoute}/{parentId}", await context.ParentETag(parentId!.Value));
        context.RequireStatus(deleted, 204, "DELETE of the parent");

        var created = await context.Send(HttpMethod.Post, context.CreateRoute(parentId), context.Body(Guid.NewGuid(), parentId, context.Values(0)));

        context.RequireStatus(created, 410, "Create under a tombstoned parent");
    }
}
