# Project notes

## Test conventions (.NET)
- Test methods are named `Given<State>_When<Action>_Then<Outcome>`.
- Test helper methods start with `Given`, `When` or `Then` and never end in `Async`; tests read as BDD specifications.
- The `SqliteConnection` and `DbContext` are created in `IAsyncLifetime.InitializeAsync` and disposed in `DisposeAsync`, never inside a test method.
- Seed children through the parent's navigation collection. Seed with a separate short-lived context so the context under test tracks nothing until the test loads entities.
- EF queries follow the `ef-linq` skill; tests follow the `unit-testing` skill. The skills are the source of truth, and `unit-testing/PROGRESS.md` and `PROGRESS.md` hold the evidence for each rule.

## Commands
- .NET tests: `dotnet test dotnet/EfFixtures.Tests`
- Build the eval runner (needed before the reference tests): `npm run build:runner`; build the API conformance CLI (api cases and their reference tests): `npm run build:api`
- TypeScript tests: `npm test`
- ef-linq evals use `eval.config.json`; unit-testing evals use `unit-testing/eval.config.json`. Pass it with `--config`.
- `node dist/src/cli.js <baseline|run|analyze|trigger|change>` after `npx tsc`. `run` accepts `--models`, `--lines`, `--trials`, `--concurrency`. `trigger` accepts `--split tune|holdout` and `--variants`.

## Evaluation process rules
- One iteration is one commit, one `logs/` entry and one narrative section in `PROGRESS.md`.
- Never delete runs or logs. Move discarded runs to `runs/discarded/` and keep the log line with `"discarded": true` and a reason.
- The held-out trigger set is run once per description. Spec changes made after seeing results are recorded as such.
- A skill line is added only when a check shows significant failure on at least one model without it (one-sided binomial against a 0.05 noise ceiling; 3 or more failures in 14), and is kept only if the with/without ablation is significant (Fisher exact, p < 0.05, delta >= 0.3).
- Checks that models already pass stay regression monitors, not skill lines.
