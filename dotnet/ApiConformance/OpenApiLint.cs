namespace ApiConformance;

public sealed record LintResult(string Id, bool Passed, string Evidence);

public static class OpenApiLint
{
    private const string Problem = "application/problem+json";

    public static readonly IReadOnlyList<string> Ids =
    [
        "write-ops-require-if-match", "write-ops-declare-428", "put-declares-412-and-404", "put-and-delete-declare-no-conflicting-gaps",
        "delete-no-404", "create-declares-201-200-409", "read-declares-304-404-410", "error-responses-problem-json", "ids-format-uuid",
        "no-int-id-params", "operation-ids-unique", "no-server-owned-fields-in-schemas", "request-bodies-required", "routes-cover-descriptor"
    ];

    private sealed record Target(string Collection, string Item, string Create, bool Child);

    private static IEnumerable<Target> Targets(DomainDescriptor domain)
    {
        var item = (string collection) => $"{OpenApiDoc.Normalize(collection)}/{{}}";
        if (domain.Parent is { } parent)
        {
            yield return new(OpenApiDoc.Normalize(parent.CollectionRoute), item(parent.CollectionRoute), OpenApiDoc.Normalize(parent.CollectionRoute), false);
            yield return new(OpenApiDoc.Normalize(domain.CollectionRoute), item(domain.CollectionRoute), OpenApiDoc.Normalize(parent.ChildCollectionRoute), true);
        }
        else
            yield return new(OpenApiDoc.Normalize(domain.CollectionRoute), item(domain.CollectionRoute), OpenApiDoc.Normalize(domain.CollectionRoute), false);
    }

    private static IEnumerable<OpenApiOperation> Find(OpenApiDoc doc, string method, IEnumerable<string> routes) =>
        doc.Operations.Where(o => o.Method == method && routes.Contains(OpenApiDoc.Normalize(o.Path)));

    private static bool IsIdLike(string name) => name == "id" || name.EndsWith("Id", StringComparison.Ordinal);

    private static LintResult Result(string id, IEnumerable<string> violations) =>
        violations.ToList() is { Count: 0 } ? new(id, true, "ok") : new(id, false, string.Join("; ", violations));

    private static IEnumerable<string> Missing(IEnumerable<OpenApiOperation> operations, string what, Func<OpenApiOperation, bool> declares) =>
        operations.Where(o => !declares(o)).Select(o => $"{o.Key} {what}");

    public static IReadOnlyList<LintResult> Run(OpenApiDoc doc, DomainDescriptor domain)
    {
        var targets = Targets(domain).ToList();
        var items = targets.Select(t => t.Item).ToList();
        var puts = Find(doc, "PUT", items).ToList();
        var deletes = Find(doc, "DELETE", items).ToList();
        var gets = Find(doc, "GET", items).ToList();
        var creates = targets.SelectMany(t => Find(doc, "POST", [t.Create]).Select(o => (Operation: o, t.Child))).ToList();
        var routed = doc.Operations.Where(o => targets.Any(t => OpenApiDoc.Normalize(o.Path) == t.Item || OpenApiDoc.Normalize(o.Path) == t.Create)).ToList();
        var writes = puts.Concat(deletes).ToList();
        var idParameters = routed.SelectMany(o => o.Parameters.Where(p => IsIdLike(p.Name)).Select(p => (Operation: o, Parameter: p))).ToList();
        var parentField = domain.Parent?.ParentBodyField;
        var serverOwned = domain.ServerOwnedFields.Select(f => f.ToLowerInvariant()).ToHashSet();
        var expectedRoutes = targets.SelectMany(t => new[] { ("POST", t.Create), ("GET", t.Item), ("PUT", t.Item), ("DELETE", t.Item) }).Distinct().ToList();
        var operationIds = doc.Operations.Where(o => o.OperationId is not null).GroupBy(o => o.OperationId!).Where(g => g.Count() > 1).ToList();

        return
        [
            Result("write-ops-require-if-match", Missing(writes, "has no required If-Match header parameter", o => o.Header("If-Match") is { Required: true })),
            Result("write-ops-declare-428", Missing(writes, "does not declare 428", o => o.Declares(428))),
            Result("put-declares-412-and-404", puts.SelectMany(o => new[] { 412, 404 }.Where(s => !o.Declares(s)).Select(s => $"{o.Key} does not declare {s}"))),
            Result("put-and-delete-declare-no-conflicting-gaps",
                puts.SelectMany(o => new[] { 204, 410 }.Where(s => !o.Declares(s)).Select(s => $"{o.Key} does not declare {s}"))
                    .Concat(deletes.SelectMany(o => new[] { 204, 412 }.Where(s => !o.Declares(s)).Select(s => $"{o.Key} does not declare {s}")))),
            Result("delete-no-404", deletes.Where(o => o.Declares(404)).Select(o => $"{o.Key} declares 404")),
            Result("create-declares-201-200-409", creates.SelectMany(c => new[] { 201, 200, 409, 410 }.Concat(c.Child ? [404] : []).Where(s => !c.Operation.Declares(s)).Select(s => $"{c.Operation.Key} does not declare {s}"))),
            Result("read-declares-304-404-410", gets.SelectMany(o => new[] { 304, 404, 410 }.Where(s => !o.Declares(s)).Select(s => $"{o.Key} does not declare {s}"))),
            Result("error-responses-problem-json", routed.SelectMany(o => o.Responses
                .Where(r => r.Status.StartsWith('4') && !r.Content.ContainsKey(Problem)).Select(r => $"{o.Key} {r.Status} does not declare {Problem}"))),
            Result("ids-format-uuid", idParameters.Where(p => p.Parameter.In == "path" && p.Parameter.Schema is not { IsUuid: true }).Select(p => $"{p.Operation.Key} path parameter {p.Parameter.Name} is not string/uuid")
                .Concat(doc.ComponentSchemas.SelectMany(s => s.Value.Properties.Where(p => (IsIdLike(p.Key) || p.Key == parentField) && !p.Value.IsUuid).Select(p => $"schema {s.Key}.{p.Key} is not string/uuid")))),
            Result("no-int-id-params", idParameters.Where(p => p.Parameter.Schema?.NonNullTypes.Any(t => t == "integer") == true).Select(p => $"{p.Operation.Key} {p.Parameter.In} parameter {p.Parameter.Name} is an integer")),
            Result("operation-ids-unique", operationIds.Select(g => $"operationId '{g.Key}' is used by {string.Join(", ", g.Select(o => o.Key))}")),
            Result("no-server-owned-fields-in-schemas", doc.AllSchemas().SelectMany(s => s.Properties.Keys).Where(k => serverOwned.Contains(k.ToLowerInvariant())).Distinct().Select(k => $"a schema declares the server-owned property '{k}'")),
            Result("request-bodies-required", routed.Where(o => o.Method is "POST" or "PUT" && o.RequestBody is not { Required: true }).Select(o => $"{o.Key} has no required request body")),
            Result("routes-cover-descriptor", expectedRoutes.Where(e => !doc.Operations.Any(o => o.Method == e.Item1 && OpenApiDoc.Normalize(o.Path) == e.Item2)).Select(e => $"{e.Item1} {e.Item2} is not in the document"))
        ];
    }

    public static IReadOnlyList<string> Failing(IEnumerable<LintResult> results) => results.Where(r => !r.Passed).Select(r => r.Id).Order().ToList();
}
