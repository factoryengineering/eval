# Idempotent-writes skill: evaluation-driven log

Process rules (from the ef-linq certification notes): one iteration is one commit, one `logs/` entry and one section here, written together. Nothing is deleted from `logs/` or `runs/`; discarded runs are archived and marked. Spec changes made after seeing results are recorded as such.

Oracle spec: `evals/protocol.md` (domain-neutral). No skill exists yet.

## Iteration A1 - HTTP conformance host

- 2026-11-11T03:15:19.000Z Scope: infrastructure only. No protocol scenarios, reference implementations or OpenAPI work.
- `dotnet/ApiConformance`: `GeneratedApp.Compile` (Roslyn, references taken from the host's TRUSTED_PLATFORM_ASSEMBLIES so ASP.NET Core, EF Core and SQLite resolve without hard-coded paths; structured `CompileResult`/`CompileDiagnostic`, never throws on compile errors), `ApiHost` (TestServer, controllers from the generated assembly via AddApplicationPart, `AppSetup.ConfigureServices(IServiceCollection, string)` seam, EnsureCreated for DbContext types declared by the generated assembly and registered in the container), `HttpTrace`, `DomainDescriptor` with JSON loader.
- Decision: generated code is compiled without implicit usings; models must write their own `using` lines (found by the fixture failing on `Any`).
- Decision: one shared in-memory SQLite database per host, using a unique named shared-cache URI kept alive by one open keeper connection, so every DbContext scope sees the same data and two hosts never do.
- Decision: the generated assembly is loaded into the default context under a unique name per compile (no unloading); disposal stops the host and closes the keeper connection, which drops the database.
- Decision: `DomainDescriptor` loader rejects fields with fewer than 3 distinct samples.
- 2026-11-11T03:15:19.000Z Verification: `dotnet test dotnet/ApiConformance.Tests` 10 passed (compile+host+request, structured compile errors, controller discovery, isolation between hosts, disposal, descriptor loading); `dotnet test dotnet/EfFixtures.Tests` 39 passed; `npm test` 49 passed.

## Iteration A2 - create scenarios and references

- 2026-11-11T03:21:41.000Z Scope: create group only. Added `ProtocolScenarios` (scenarios, `ScenarioContext` with transcript, per-scenario fresh host runner), `CanonicalHash`, the complete good reference under `evals/subjects/blog/` (Entities, Service, Controllers, AppSetup: the whole protocol, though only create is asserted), descriptors `evals/domains/blog.json` (Post with parent Blog) and `blog-root.json` (Blog, no parent), and `evals/mutants.json`.
- Decision: `ParentDescriptor` gained an optional `WritableFields` list so the harness can create a parent without domain knowledge. `childCollectionRoute` uses the `{parentId}` placeholder; `collectionRoute` is where GET/PUT/DELETE of the resource live.
- Decision (ambiguity in protocol.md): canonical JSON string escaping is "escape only quote, backslash and control characters"; short forms (\b \f \n \r \t) versus \u00xx were unspecified. Chose the JSON.stringify/RFC 8785 rule: short forms where they exist, otherwise lowercase \u00xx. Numbers are written as invariant integers, booleans as true/false. Vectors in `CanonicalHashTests` were computed independently with Python.
- Decision (ambiguity): replay of a create returns 200 (first create 201); scenarios accept any 2xx and compare the body fields and ETag, not bytes or exact status.
- Decision: good reference tests run against both descriptors (child and root); mutants run against the child descriptor only and mutate PostService.
- Decision: the reference returns `post.ETag` from the entity on first create, so the mutant that sets a non-hash ETag stays consistent between POST and GET and fails only the canonical-hash scenario.
- Mutant fixes after first run (recorded, not hidden): `replay-compares-current-state` first compared titles, which also ignored the parent and failed the different-parent scenario; changed to compare the current ETag (hash of current representation) with the request hash, which fails only replay-after-PUT. `wrong-etag` initially expected create-succeeds to fail too; it does not, since POST and GET agree on the (wrong) ETag, so expectation was corrected. `server-generates-id` expectation was set to the five scenarios that legitimately depend on the client id.
- Mutant results (create group, blog.json): replay-409 -> create-replay-returns-same-success, create-replay-after-put-returns-current-representation; replay-compares-current-state -> create-replay-after-put-returns-current-representation; server-generates-id -> create-succeeds, create-replay-returns-same-success, create-replay-after-put-returns-current-representation, create-same-id-different-body-conflicts, child-create-same-id-different-parent-conflicts; no-parent-check-on-replay -> child-create-same-id-different-parent-conflicts; wrong-etag -> etag-is-canonical-hash; missing-id-accepted -> create-missing-id-is-bad-request.
- TODO scenario list for later iterations: etag-independent-of-server-owned-fields (change Views through the DbContext, ETag and replay must not change), GET/304, PUT (428/412/replay-as-success/400/409 immutable parent), DELETE (tombstone, dependants, idempotent 204, 410 on replay), races.
- 2026-11-11T03:21:41.000Z Verification: `dotnet test dotnet/ApiConformance.Tests` 24 passed; `dotnet test dotnet/EfFixtures.Tests` 39 passed; `npm test` 49 passed.
