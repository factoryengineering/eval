"""Generates cases.json and references.json for the idempotent-writes and api-design homes.

Run from the repository root: python3 idempotent-writes/evals/generate.py [--catalog]
Cases are 3 domains x 3 prompt-specificity levels per home; the prompts are identical across homes, only `graded` differs.
References: good = the domain's reference subject; bad = mutants.json entries applied to the subject (attached to the level-3 case).
"""
import glob
import json
import subprocess
import sys

EVALS = "idempotent-writes/evals"
CLI = "dotnet/ApiConformance.Cli/bin/Release/net10.0/ApiConformance.Cli.dll"

DOMAINS = {
    "blog": {"subject": "blog", "noun": "post", "plural": "posts", "parent": "blog", "parentPlural": "blogs"},
    "support": {"subject": "support", "noun": "support ticket", "plural": "support tickets"},
    "logistics": {"subject": "logistics", "noun": "shipment", "plural": "shipments", "parent": "warehouse", "parentPlural": "warehouses"},
}
LEVELS = {1: "naked", 2: "mild", 3: "specified"}

PREAMBLE = "Implement an HTTP API with ASP.NET Core controllers and EF Core (SQLite) to create, read, update and delete {plural}"
RETRY = "Clients may retry requests after timeouts and several clients may edit the same item concurrently."

IDEMPOTENT_GRADED = [
    "scenario:create", "scenario:replace", "scenario:delete", "scenario:race", "scenario:lost-response", "scenario:canonical", "scenario:server-owned",
    "static:public-id-unique-index", "static:etag-concurrency-token", "static:creation-hash-immutable", "static:tombstone-property",
    "static:no-rowversion-etag", "static:server-owned-not-concurrency-token", "static:etag-column-not-null",
]
API_DESIGN_GRADED = [
    "scenario:read",
    "scenario:replace-without-if-match-is-precondition-required",
    "scenario:replace-without-if-match-on-unknown-id-is-precondition-required",
    "scenario:delete-without-if-match-is-precondition-required",
    "scenario:replace-stale-if-match-different-body-is-precondition-failed",
    "scenario:delete-stale-if-match-is-precondition-failed",
    "scenario:race-put-different-body-same-etag-one-precondition-failed",
    "scenario:create-same-id-different-body-conflicts",
    "scenario:child-create-same-id-different-parent-conflicts",
    "scenario:child-replace-different-parent-conflicts",
    "scenario:race-create-same-id-different-body-one-conflicts",
    "static:routes-use-guid", "static:internal-key-int-hidden", "static:no-int-id-in-dtos", "static:no-entity-in-responses", "static:etag-header-set",
    "static:if-match-required", "static:problem-details-used", "static:cancellation-token-passed", "static:sha256-used-for-etag",
    "lint", "spec",
]
HOMES = {"idempotent-writes": IDEMPOTENT_GRADED, "api-design": API_DESIGN_GRADED}

BAD = {
    "idempotent-writes": {
        "blog": ["server-generates-id", "replay-409", "put-stale-same-body-412", "delete-404-on-repeat", "check-then-insert", "put-no-retry-on-concurrency-exception",
                 "delete-concurrency-500", "etag-property-order-sensitive", "hash-includes-server-owned", "tombstone-hard-deletes", "no-unique-index",
                 "no-concurrency-token", "rowversion-etag", "no-tombstone-filter", "creation-hash-mutable", "etag-column-nullable"],
        "support": ["support-etag-property-order-sensitive", "support-server-owned-concurrency-token"],
        "logistics": ["logistics-replay-409", "logistics-delete-concurrency-500"],
    },
    "api-design": {
        "blog": ["put-without-if-match-allowed", "delete-without-if-match-allowed", "put-ignores-if-match", "no-parent-check-on-replay", "put-allows-parent-change",
                 "get-ignores-if-none-match", "entity-returned", "int-id-in-route", "int-id-in-dto", "etag-header-missing", "if-match-optional",
                 "no-cancellation-token", "problem-details-absent", "md5-etag", "openapi-delete-no-428", "openapi-put-no-410", "openapi-error-not-problem-json",
                 "openapi-int-id-query-param", "spec-put-409-undeclared", "spec-response-schema-wrong-type", "openapi-route-hidden",
                 "openapi-put-no-404", "openapi-delete-declares-404", "openapi-create-no-200", "openapi-get-no-304", "openapi-server-owned-in-request-schema",
                 "openapi-request-body-optional"],
        "support": ["guid-primary-key", "support-spec-delete-204-undeclared", "support-spec-response-has-undeclared-property"],
        "logistics": ["logistics-delete-404-on-repeat"],
    },
}

CLR = {"System.String": "string", "System.Int32": "int", "System.Decimal": "decimal", "System.Boolean": "bool"}


def descriptor(name):
    return json.load(open(f"{EVALS}/domains/{name}.json"))


def pascal(name):
    return name[0].upper() + name[1:]


def describe(fields, extra):
    parts = ["`id` (a GUID)"] + extra + [f"`{f['name']}` ({CLR[f['clrType']]})" for f in fields]
    return ", ".join(parts[:-1]) + f" and {parts[-1]}" if len(parts) > 1 else parts[0]


def server_owned(d):
    by_entity = {}
    for s in d["serverOwned"]:
        kind = "a nullable DateTime timestamp" if s.get("kind") == "timestamp" else "an integer counter"
        by_entity.setdefault(s["entityTypeName"], []).append(f"`{pascal(s['field'])}`, {kind}")
    rows = [f"`{entity}` also stores {' and '.join(fields)}" for entity, fields in by_entity.items()]
    return "; ".join(rows) + ", maintained by the server and never set by clients."


def environment(name, d):
    p = d.get("parent")
    entities = f"`{p['entityTypeName']}` and `{d['entityTypeName']}`" if p else f"`{d['entityTypeName']}`"
    return (
        "Environment: .NET 10, ASP.NET Core controllers and EF Core with the SQLite provider (Microsoft.EntityFrameworkCore.Sqlite); no other packages. "
        "Implicit usings (the ones of the Microsoft.NET.Sdk.Web template: System, System.Collections.Generic, System.IO, System.Linq, System.Net.Http, System.Threading, System.Threading.Tasks and the ASP.NET Core and Microsoft.Extensions Builder, Hosting, Http, Routing, Configuration, DependencyInjection and Logging namespaces) and nullable reference types are enabled; write every other `using` directive you need. "
        "Ids in routes and in request and response bodies are GUIDs. JSON property names are camelCase. "
        f"Name the EF entity classes exactly {entities}, because the grader inspects them. {server_owned(d)} "
        "Provide a public static class `AppSetup` with `public static void ConfigureServices(IServiceCollection services, string sqliteConnectionString)` "
        "that registers your DbContext (using that connection string) and every service your code needs; do not write a Program.cs or build a WebApplication. "
        "The grader hosts your code in-process (ASP.NET Core TestServer): it calls `AppSetup.ConfigureServices`, adds and maps your controllers, creates the schema "
        "with `Database.EnsureCreated()` on every registered DbContext (there are no migrations), and drives the API over HTTP. "
        "Return the code as one or more fenced C# code blocks (```csharp), for example one per file; all blocks are compiled together as one assembly."
    )


def resources(name, d):
    meta = DOMAINS[name]
    p = d.get("parent")
    lines = []
    if p:
        pf = describe(p["writableFields"], [])
        lines.append(f"- A {meta['parent']} is represented as JSON with {pf}. Routes: `POST {p['collectionRoute']}`; `GET`, `PUT` and `DELETE {p['collectionRoute']}/{{id}}`.")
        rf = describe(d["writableFields"], [f"`{p['parentBodyField']}` (the GUID of its {meta['parent']})"])
        child = p["childCollectionRoute"].replace("{parentId}", "{" + p["parentBodyField"] + "}")
        lines.append(f"- A {meta['noun']} belongs to a {meta['parent']} and is represented as JSON with {rf}. Routes: `POST {child}` creates one under a {meta['parent']}; `GET`, `PUT` and `DELETE {d['collectionRoute']}/{{id}}`.")
    else:
        rf = describe(d["writableFields"], [])
        lines.append(f"- A {meta['noun']} is represented as JSON with {rf}. Routes: `POST {d['collectionRoute']}`; `GET`, `PUT` and `DELETE {d['collectionRoute']}/{{id}}`.")
    return "\n".join(lines)


def requirements(name, d):
    meta = DOMAINS[name]
    n, p = meta["noun"], d.get("parent")
    create_child = (
        f" The body's {d['parent']['parentBodyField']} must equal the {meta['parent']} in the route (otherwise 400); a missing {meta['parent']} is 404 and a deleted one is 410; "
        f"the same id under a different {meta['parent']} is 409."
        if p else ""
    )
    delete_child = f" Deleting a {meta['parent']} also deletes its {meta['plural']} in the same transaction." if p else ""
    immutable = f" Moving a {n} to another {meta['parent']} through PUT is 409." if p else ""
    return (
        "Requirements:\n"
        f"1. Create: the client chooses the GUID `id` and sends it in the POST body; the server never generates public ids. The first successful create returns 201 with a `Location` and an `ETag` header. "
        f"Repeating a create with the same id and the same content succeeds (200 with the current representation and ETag), a replay is never an error. The same id with different content is 409; a missing id or an invalid body is 400; "
        f"an id that belongs to a deleted item is 410.{create_child}\n"
        "2. Read: GET returns 200 with the `ETag` header; a matching `If-None-Match` returns 304; an unknown id is 404 and a deleted item is 410.\n"
        "3. ETag: the quoted unpadded base64url SHA-256 of the canonical JSON of the representation, which is the id, the parent id where there is one, and the client-writable fields in declared order; compact JSON, lowercase GUIDs, "
        "NFC-normalized strings, numbers without trailing zeros; in strings only the quote, the backslash and control characters are escaped (`\\b \\f \\n \\r \\t` where they exist, otherwise lowercase `\\u00xx`), everything else is written literally. Two spellings of the same content give the same ETag. "
        "Server-owned values (counters, timestamps and other values the server maintains) are never part of the representation or the hash and changing them never changes the ETag or invalidates an If-Match.\n"
        f"4. Update: PUT replaces the whole item; the body id must equal the route id (otherwise 400); the `If-Match` header is required and a missing one is 428, but validation comes first: an invalid or malformed body, or a body id that differs from the route id, is 400 even when `If-Match` is missing. If the current ETag already equals the hash of the PUT body "
        "the change is already applied and the answer is 204 with that ETag in the `ETag` header, whatever `If-Match` says; so a PUT that changes nothing is 204 and carries the unchanged ETag. Otherwise a matching `If-Match` (or `*`) updates the item and returns 204 with the new ETag; a stale one is 412 with the current ETag. "
        f"Unknown id is 404, deleted is 410.{immutable} Two writers racing from the same ETag never get a 5xx. If their bodies differ, one wins and the other gets 412. If the two PUTs are identical, both succeed with 204 and the same ETag: the second is treated as already applied, never 412.\n"
        f"5. Delete: `If-Match` is required (428, even for an unknown id). An unknown or already deleted item answers 204, never 404. A live item with a stale `If-Match` is 412. Otherwise the item is soft-deleted with a tombstone timestamp, so later reads are 410.{delete_child}\n"
        "6. Storage: a unique index on the public GUID; an internal int primary key that never leaves the service; the ETag stored in a non-null column used as the EF concurrency token (never a row version or sequence number); "
        "the hash of the create request stored immutably (configured in EF so the column is never updated after insert, `AfterSaveBehavior` Throw or Ignore) so a replay can be told from a conflict; a nullable tombstone column with a global query filter. "
        "Create by inserting and classifying a unique violation, not by checking first. Counters that change on reads must never be concurrency tokens.\n"
        "7. Errors are `application/problem+json` for 400, 404, 409, 410, 412 and 428.\n"
        "8. API contract: annotate every action with `[ProducesResponseType]` for each status it can return (the success type, and `ProblemDetails` with content type `application/problem+json` for errors) and declare `If-Match` "
        "as a required header parameter on PUT and DELETE, and mark the request bodies of POST and PUT as required; the OpenAPI document is generated from your code and compared with the real behavior. Never return EF entities, only response types. Pass the request `CancellationToken` through to the database calls."
    )


def prompt(name, level):
    d = descriptor(name)
    meta = DOMAINS[name]
    head = PREAMBLE.format(plural=f"{meta['parentPlural']} and {meta['plural']}" if "parent" in meta else meta["plural"])
    body = [f"{head}.", "", "Resources:", resources(name, d), "", environment(name, d)]
    if level >= 2:
        body += ["", RETRY]
    if level >= 3:
        body += ["", requirements(name, d)]
    return "\n".join(body)


def read_subject(subject):
    return {f.split("/")[-1]: open(f).read() for f in sorted(glob.glob(f"{EVALS}/subjects/{subject}/*.cs"))}


def response(files):
    return "\n\n".join("```csharp\n" + text.rstrip("\n") + "\n```" for text in files.values())


def mutate(files, mutant):
    files = dict(files)
    for e in [mutant] + mutant.get("extra", []):
        file_name = e.get("file")
        if e["find"] not in files[file_name]:
            raise SystemExit(f"mutant {mutant['name']}: find text missing in {file_name}")
        files[file_name] = files[file_name].replace(e["find"], e["replace"])
    return files


def case_id(domain, level):
    return f"{domain}-{LEVELS[level]}"


def write(path, value):
    with open(path, "w") as f:
        json.dump(value, f, indent=2)
        f.write("\n")


def catalog():
    scenarios, layers = {}, {"static": [], "lint": [], "spec": []}
    for name, domain in [("blog", "blog"), ("blog", "blog-root"), ("support", "support"), ("logistics", "logistics"), ("logistics", "logistics-root")]:
        request = json.dumps({"id": domain, "domain": descriptor(domain), "sources": list(read_subject(name).values())})
        out = subprocess.run(["dotnet", CLI], input=request + "\n", capture_output=True, text=True, check=True).stdout
        for check in json.loads(out)["checks"]:
            if check["layer"] == "scenario":
                scenarios.setdefault(check["id"], check["group"])
            elif check["id"] not in layers[check["layer"]]:
                layers[check["layer"]].append(check["id"])
    with open("src/api-catalog.ts", "w") as f:
        f.write("export const CATALOG = " + json.dumps({"scenarios": scenarios, **layers}, indent=2) + " as const;\n")


def main():
    mutants = {m["name"]: m for m in json.load(open(f"{EVALS}/mutants.json"))}
    for home, graded in HOMES.items():
        cases, references = [], {}
        for domain in DOMAINS:
            subject = read_subject(DOMAINS[domain]["subject"])
            for level in LEVELS:
                cid = case_id(domain, level)
                cases.append({"id": cid, "prompt": prompt(domain, level), "targets": [], "api": {"domain": domain, "level": level, "graded": graded}, "must": [], "mustNot": []})
                bad = {}
                if level == 3:
                    for name in BAD[home][domain]:
                        mutant = mutants[name]
                        if mutant.get("domain", "blog") != domain:
                            raise SystemExit(f"mutant {name} is for {mutant.get('domain', 'blog')}, not {domain}")
                        bad[name] = response(mutate(subject, mutant))
                references[cid] = {"good": response(subject), "bad": bad}
        write(f"{home}/evals/cases.json", cases)
        write(f"{home}/evals/references.json", references)
    if "--catalog" in sys.argv:
        catalog()


if __name__ == "__main__":
    main()
