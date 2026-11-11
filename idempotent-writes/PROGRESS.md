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
