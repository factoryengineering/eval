# EF LINQ Skill Evaluation Framework

A TypeScript framework for developing a **minimal** agent skill for Entity Framework Core LINQ query authoring, where **every line is load bearing**.

For each line of the skill it:

1. Runs the eval cases that target that line **with** the full skill and **without** that one line.
2. Computes the pass-rate delta; a line is `load-bearing` only if `with - without >= minDelta`.
3. Repeats for every configured model, to show the line helps across LLMs.
4. Records input/output token use per model per run, so iterations can be compared as the skill grows or shrinks.
5. Logs every skill change (with a snapshot) and every evaluation result.

A line that is `not-load-bearing` on every model is a candidate for deletion.

## Stack (versions verified 2026-10-26)

| Piece | Version | Notes |
| --- | --- | --- |
| Node.js | 22.x (installed 22.22.0) | Runs compiled JS, uses built-in `node:test` |
| TypeScript | 7.0.2 | Latest stable. 7.1 is beta. |
| @types/node | 22.x | Matches the Node runtime |
| .NET SDK | 10.0.100 (pinned via `global.json`, rolls forward) | |
| EF Core | 10.0.13 | Latest stable that is at least 7 days old (10.0.14 shipped 2026-10-25). EF 11 is RC. |
| Provider | Microsoft.EntityFrameworkCore.Sqlite 10.0.13 | In-memory SQLite for query tests |
| dotnet-ef | 10.0.13 | Local tool in `.config/dotnet-tools.json` (`dotnet tool restore`) |
| xunit | 2.9.3 | |

There are no runtime npm dependencies. Anthropic API calls use `fetch`.

## Layout

```
skills/ef-linq/SKILL.md    The skill under test. One rule per line: "- [id] text"
evals/cases.json           Eval cases: prompt, targeted line ids, must / mustNot regexes
eval.config.json           Models, trials per case, minDelta
src/                       skill parsing, providers, grading, runner, log store, CLI
test/                      node:test unit tests (compiled to dist/test)
dotnet/EfFixtures          Sample BlogContext + EF Core (SQLite)
dotnet/EfFixtures.Tests    xunit tests proving LINQ translates and runs on EF Core
logs/evals.jsonl           One summary per run (verdicts and token totals)
logs/changes.jsonl         One entry per logged skill edit
logs/snapshots/<hash>.md   Skill text at each logged change
runs/<runId>.json          Full per-trial record, including raw model output
```

Rule ids (`[id]`) are stripped before the skill is sent to a model. They exist so cases and logs can refer to a line stably.

## Usage

```sh
npm install
dotnet tool restore

npm test                 # compile + unit tests
npm run test:dotnet      # EF Core fixture tests

npm run eval -- --models mock                # cheap harness check
npm run eval -- --models haiku-5.5 --trials 3
npm run eval                                  # all configured models
npm run eval:change -- "Added split-query rule"   # log a skill edit + snapshot
npm run eval:compare                          # last two runs: token and verdict deltas
```

Suggested loop: edit the skill, `eval:change` with a note, `eval`, then `eval:compare`. Drop or reword lines that are not load bearing, and keep any that are.

## Providers

| `provider` | Tokens | Notes |
| --- | --- | --- |
| `anthropic` | Exact (from API `usage`) | Needs `ANTHROPIC_API_KEY`. Model ids such as `claude-haiku-5-5`. |
| `devin-cli` | **Estimated** (chars / 4) | Shells out to `devin -p --prompt-file`. Reaches many model families (`devin models`). The CLI reports no usage, so results are flagged `(estimated)`. Estimates are consistent between runs, so deltas remain meaningful. Use `anthropic` for billing-grade numbers. |
| `mock` | Estimated | Deterministic. Echoes the backticked code in the skill. It only verifies the harness and makes no claim about the skill. |

## Writing cases

```json
{
  "id": "blog-with-posts-and-tags",
  "prompt": "...Return only a C# code block.",
  "targets": ["split-query"],
  "must": ["AsSplitQuery\\(\\)"],
  "mustNot": []
}
```

Every id in `targets` is ablated against this case. A case should fail without its target line and pass with it, so choose prompts where a model would not do the right thing unprompted. Grading is deterministic regex. Models that already follow a rule unprompted produce `delta 0`, which is the signal that the line is not earning its place for that model.

## Known limits

- Small trial counts are noisy. Use `--trials 5` or more before deleting a line, and treat a single-trial run as a smoke test.
- Regex graders check form, not correctness. A planned next step is to compile and run generated queries against `dotnet/EfFixtures` (SQLite) and assert on `ToQueryString()`.
- `with` runs are shared across lines of the same case, so token totals count each unique call once.

## First test run (2026-10-26)

`haiku-5.5` via `devin-cli`, 1 trial, 5-line skill: `no-tracking` and `split-query` were load bearing (0 -> 1.00). `project`, `translatable` and `async` showed no effect on this small sample, so they need harder cases or more trials before any decision. See `logs/` and `runs/`.
