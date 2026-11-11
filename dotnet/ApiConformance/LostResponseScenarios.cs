namespace ApiConformance;

public static partial class ProtocolScenarios
{
    public const string LostResponseGroup = "lost-response";

    public static IReadOnlyList<Scenario> LostResponse(DomainDescriptor domain)
    {
        var scenarios = new List<Scenario>
        {
            new("lost-response-create-retry-succeeds", LostResponseGroup, LostCreateRetry),
            new("lost-response-put-retry-succeeds", LostResponseGroup, LostPutRetry),
            new("lost-response-delete-retry-succeeds", LostResponseGroup, LostDeleteRetry),
            new("duplicate-delivery-create-succeeds-twice", LostResponseGroup, DuplicateCreate),
            new("duplicate-delivery-put-succeeds-twice", LostResponseGroup, DuplicatePut),
            new("duplicate-delivery-delete-succeeds-twice", LostResponseGroup, DuplicateDelete)
        };
        if (domain.Parent is not null) scenarios.Add(new("lost-response-parent-create-retry-succeeds", LostResponseGroup, LostParentCreateRetry));
        return scenarios;
    }

    private static async Task LostCreateRetry(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var values = context.Values(0);
        var create = context.Post(parentId, id, values);
        await context.SendAndLoseResponse(create);

        var retry = await context.Send(create);

        context.RequireSuccess(retry, "Retry of a create whose response was lost");
        context.Require(retry.Header("ETag") == ScenarioContext.Quoted(context.Hash(id, parentId, values)), "The retry must return the ETag of the stored representation.");
        context.RequireRepresentation(retry, id, parentId, values, "Retry body");
        await context.RequireSingleResource(id, "After the lost create and its retry");
    }

    private static async Task LostParentCreateRetry(ScenarioContext context)
    {
        var parent = context.Domain.Parent!;
        var create = new Call(HttpMethod.Post, parent.CollectionRoute, context.ParentBody(Guid.NewGuid()));
        await context.SendAndLoseResponse(create);

        var retry = await context.Send(create);

        context.RequireSuccess(retry, "Retry of a parent create whose response was lost");
        context.RequireQuotedETag(retry, "Retry of a parent create");
    }

    private static async Task LostPutRetry(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var changed = context.Values(1);
        var put = context.PutCall(resource.Id, resource.ParentId, changed, resource.ETag);
        await context.SendAndLoseResponse(put);

        var retry = await context.Send(put);

        context.RequireStatus(retry, 204, "Retry of a PUT whose response was lost");
        context.Require(retry.Header("ETag") == ScenarioContext.Quoted(context.Hash(resource.Id, resource.ParentId, changed)), "The retried PUT must return the ETag of the new representation.");
        await context.RequireFinalState(resource.Id, resource.ParentId, changed, "After the lost PUT and its retry");
    }

    private static async Task LostDeleteRetry(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var delete = context.DeleteCall(resource.Id, resource.ETag);
        await context.SendAndLoseResponse(delete);

        var retry = await context.Send(delete);

        context.RequireStatus(retry, 204, "Retry of a DELETE whose response was lost");
        context.RequireStatus(await context.Get(resource.Id), 410, "GET after the lost DELETE and its retry");
    }

    private static async Task DuplicateCreate(ScenarioContext context)
    {
        var parentId = await context.CreateParent();
        var id = Guid.NewGuid();
        var create = context.Post(parentId, id, context.Values(0));

        var first = await context.Send(create);
        var second = await context.Send(create);

        context.RequireSuccess(first, "First delivery of the create");
        context.RequireSuccess(second, "Second delivery of the create");
        context.Require(first.Header("ETag") == second.Header("ETag"), "Both deliveries must return the same ETag.");
        await context.RequireSingleResource(id, "After two deliveries of the same create");
    }

    private static async Task DuplicatePut(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var changed = context.Values(1);
        var put = context.PutCall(resource.Id, resource.ParentId, changed, resource.ETag);

        var first = await context.Send(put);
        var second = await context.Send(put);

        context.RequireStatus(first, 204, "First delivery of the PUT");
        context.RequireStatus(second, 204, "Second delivery of the PUT");
        context.Require(first.Header("ETag") == second.Header("ETag"), "Both deliveries must return the same ETag.");
    }

    private static async Task DuplicateDelete(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var delete = context.DeleteCall(resource.Id, resource.ETag);

        var first = await context.Send(delete);
        var second = await context.Send(delete);

        context.RequireStatus(first, 204, "First delivery of the DELETE");
        context.RequireStatus(second, 204, "Second delivery of the DELETE");
    }
}
