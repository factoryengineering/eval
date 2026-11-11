namespace ApiConformance;

public static partial class ProtocolScenarios
{
    public const string ServerOwnedGroup = "server-owned";

    public static IReadOnlyList<Scenario> ServerOwned(DomainDescriptor domain) =>
        domain.ServerOwned is { Count: > 0 }
            ?
            [
                new("server-owned-change-does-not-change-etag", ServerOwnedGroup, ServerOwnedChangeDoesNotChangeETag),
                new("server-owned-change-does-not-invalidate-if-match", ServerOwnedGroup, ServerOwnedChangeDoesNotInvalidateIfMatch),
                new("server-owned-field-is-absent-from-representation", ServerOwnedGroup, ServerOwnedFieldIsAbsentFromRepresentation)
            ]
            : [];

    private static async Task ServerOwnedChangeDoesNotChangeETag(ScenarioContext context)
    {
        foreach (var field in context.Domain.ServerOwned!)
        {
            var resource = await context.CreateResource();
            var before = await context.Get(resource.Id);
            var parentBefore = field.OwnedByParent ? await context.ParentETag(resource.ParentId!.Value) : null;

            await context.Host.BumpServerOwned(field);
            var after = await context.Get(resource.Id);

            context.RequireStatus(after, 200, $"GET after bumping '{field.Field}'");
            context.Require(after.Header("ETag") == before.Header("ETag"), $"Bumping '{field.Field}' changed the ETag from {before.Header("ETag")} to {after.Header("ETag")}.");
            if (field.OwnedByParent)
            {
                var parentAfter = await context.ParentETag(resource.ParentId!.Value);
                context.Require(parentAfter == parentBefore, $"Bumping '{field.Field}' changed the parent ETag from {parentBefore} to {parentAfter}.");
            }
        }
    }

    private static async Task ServerOwnedChangeDoesNotInvalidateIfMatch(ScenarioContext context)
    {
        foreach (var field in context.Domain.ServerOwned!)
        {
            var resource = await context.CreateResource();
            await context.Host.BumpServerOwned(field);
            var changed = context.Values(1);

            var put = await context.Put(resource.Id, context.Body(resource.Id, resource.ParentId, changed), resource.ETag);

            context.RequireStatus(put, 204, $"PUT with the pre-bump If-Match after bumping '{field.Field}'");
            var read = await context.Get(resource.Id);
            context.RequireRepresentation(read, resource.Id, resource.ParentId, changed, "GET body after PUT");
        }
    }

    private static async Task ServerOwnedFieldIsAbsentFromRepresentation(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var read = await context.Get(resource.Id);
        foreach (var field in context.Domain.ServerOwned!)
        {
            if (field.OwnedByParent)
            {
                var parentRead = await context.Send(HttpMethod.Get, $"{context.Domain.Parent!.CollectionRoute}/{resource.ParentId}");
                context.RequireNoServerOwnedField(parentRead, field.Field, "Parent GET response");
                continue;
            }
            context.RequireNoServerOwnedField(resource.Created, field.Field, "Create response");
            context.RequireNoServerOwnedField(read, field.Field, "GET response");
        }
    }
}
