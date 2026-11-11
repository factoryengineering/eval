namespace ApiConformance;

public static partial class ProtocolScenarios
{
    public const string ReadGroup = "read";

    public static IReadOnlyList<Scenario> Read(DomainDescriptor domain) =>
    [
        new("read-returns-representation-and-etag", ReadGroup, ReadReturnsRepresentationAndETag),
        new("read-matching-if-none-match-is-not-modified", ReadGroup, ReadMatchingIfNoneMatchIsNotModified),
        new("read-non-matching-if-none-match-returns-representation", ReadGroup, ReadNonMatchingIfNoneMatchReturnsRepresentation),
        new("read-unknown-id-is-not-found", ReadGroup, ReadUnknownIdIsNotFound),
        new("read-tombstoned-is-gone", ReadGroup, ReadTombstonedIsGone)
    ];

    private static async Task ReadReturnsRepresentationAndETag(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var read = await context.Get(resource.Id);

        context.RequireStatus(read, 200, "GET");
        context.RequireRepresentation(read, resource.Id, resource.ParentId, resource.Values, "GET body");
        var expected = ScenarioContext.Quoted(context.Hash(resource.Id, resource.ParentId, resource.Values));
        context.Require(read.Header("ETag") == expected, $"GET ETag must be the canonical hash {expected}, got {read.Header("ETag")}.");
    }

    private static async Task ReadMatchingIfNoneMatchIsNotModified(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var read = await context.Get(resource.Id, resource.ETag);

        context.RequireStatus(read, 304, "GET with a matching If-None-Match");
    }

    private static async Task ReadNonMatchingIfNoneMatchReturnsRepresentation(ScenarioContext context)
    {
        var resource = await context.CreateResource();

        var read = await context.Get(resource.Id, ScenarioContext.StaleETag);

        context.RequireStatus(read, 200, "GET with a non-matching If-None-Match");
        context.RequireRepresentation(read, resource.Id, resource.ParentId, resource.Values, "GET body");
    }

    private static async Task ReadUnknownIdIsNotFound(ScenarioContext context)
    {
        var read = await context.Get(Guid.NewGuid());

        context.RequireStatus(read, 404, "GET of an unknown id");
    }

    private static async Task ReadTombstonedIsGone(ScenarioContext context)
    {
        var resource = await context.CreateResource();
        var deleted = await context.Delete(context.ItemRoute(resource.Id), resource.ETag);
        context.RequireStatus(deleted, 204, "DELETE with the current ETag");

        var read = await context.Get(resource.Id);

        context.RequireStatus(read, 410, "GET of a tombstoned resource");
    }
}
