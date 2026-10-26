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
.devin/skills/ef-linq/SKILL.md   The skill under test, in the folder Devin loads project skills from. One rule per bullet, no ids
evals/skill-lines.json     Rule id -> exact bullet text, so cases and logs can refer to a line stably
evals/cases.json           Eval cases: prompt, targeted line ids, regexes, and an exec block of executable assertions
evals/references.json      A good and bad C# solution per case; a test requires the graders to accept and reject them
evals/name-labels.json     Hand-labelled identifiers used to calibrate the name judge
evals/name-verdicts.json   Cached judge verdicts, keyed by rubric hash and judge model
evals/trigger.json         Skill-discovery cases and description clauses
eval.config.json           Models, trials per case, minDelta
src/                       skill parsing, providers, grading, exec grader, judge, runner, log store, CLI
test/                      node:test unit tests (compiled to dist/test)
dotnet/EfFixtures          Sample BlogContext (Blog, Post, Contributor), a non-translatable helper, EF Core on SQLite
dotnet/EvalRunner          Roslyn runner: compiles model output, runs it on seeded SQLite, captures SQL and syntax facts
dotnet/EfFixtures.Tests    xunit tests for the fixtures and for the runner
logs/evals.jsonl           One summary per run (verdicts and token totals)
logs/changes.jsonl         One entry per logged skill edit
logs/snapshots/<hash>.md   Skill text at each logged change
runs/<runId>.json          Full per-trial record, including raw model output
```

The deployed skill contains no ids. The harness reads `evals/skill-lines.json`, tags each bullet in memory, ablates one at a time, and renders the untagged text for the "with" variant, so what is evaluated is exactly what ships. If a bullet has no id, or an id has no bullet, the run fails with an error rather than testing a stale mapping. After editing a rule, update its text in `skill-lines.json` too.

## Skill structure and why it is not classic RTCC

The skill targets a coding agent and must stay valid across projects and as schemas change. It records Entity Framework opinions only. That fixes which RTCC components can be load bearing, meaning removing them would flip an eval:

| Component | In the skill? | Where it lives and why |
| --- | --- | --- |
| **Task** | Yes | The frontmatter `description` is what the agent reads to decide whether to load the skill. It is the only text present before the skill is chosen, so it is load bearing by discovery. `npm run eval:trigger` measures it: a model sees a skill list with neighbouring skills and must pick `ef-linq` exactly when it applies. Its two clauses are load bearing on 3 of 5 models (scope) and 4 of 5 (exclusions), mainly by rejecting near-miss requests such as in-memory LINQ and raw SQL. It adds nothing for recall, because the skill name already drives that (`PROGRESS.md`, Iterations 10-11). |
| **Constraints** | Yes | The bulleted rules. Each is ablated against a targeted case and must change pass rates (see `PROGRESS.md`). This is the whole body of the skill. |
| **Context** | No | A schema in the skill would duplicate the repository, go stale as the model evolves, and tie the skill to one domain. The agent reads the entities and `DbContext` from the code, so context is supplied by the environment. The eval case prompts state the schema because there is no repository in the harness. That text belongs to the test, not the skill. |
| **Output format** | No | The agent harness owns tool-call validity and file edits. The phrase "Return only a C# code block" appears in the case prompts only so graders can extract code. It is a test fixture, not a skill instruction. |
| **Role** | No | See below. |

Headings and section labels are not added, because they carry no instruction and no eval could measure them.

### Why Role is absent

1. **No measurable effect.** Zheng et al. evaluated 162 personas across 4 LLM families and 2,410 questions and found that adding personas to system prompts "does not improve model performance" against a no-persona control, and that the effect of any one persona is "largely random" (Findings of EMNLP 2024). A role line would fail this project's own test: it would show no delta when ablated.
2. **The positive result does not apply.** Kong et al. found role-play prompting beat standard zero-shot prompting on reasoning benchmarks (NAACL 2024). Those gains came from a purpose-built multi-turn role immersion on reasoning tasks such as AQuA and Last Letter. They are not evidence that a one-line "You are an EF expert" helps code style rules. If an ablation ever shows a role line moving a case, it can be added back under the same load-bearing test.
3. **Extra text has a cost.** Shi et al. showed that irrelevant context dramatically lowers model accuracy (ICML 2023). Levy et al. showed reasoning accuracy degrading at input lengths far below the technical maximum, with padding as the controlled variable (ACL 2024). Every unmeasured line in a skill is padding that the agent carries into every session where the skill loads.
4. **Formatting is not free.** Sclar et al. found that meaning-preserving formatting changes alone shifted accuracy by up to 76 points on some models and correlated only weakly across models (ICLR 2024). Added structure is therefore a risk to be measured, not a safe default. That is why structure here is admitted only when an ablation shows it changing behavior, and why the skill is re-run on several models.

### References

- Zheng, M., Pei, J., Logeswaran, L., Lee, M., and Jurgens, D. (2024). When "A Helpful Assistant" Is Not Really Helpful: Personas in System Prompts Do Not Improve Performances of Large Language Models. Findings of EMNLP 2024. https://aclanthology.org/2024.findings-emnlp.888/
- Kong, A., Zhao, S., Chen, H., et al. (2024). Better Zero-Shot Reasoning with Role-Play Prompting. NAACL 2024. https://aclanthology.org/2024.naacl-long.228/
- Shi, F., Chen, X., Misra, K., et al. (2023). Large Language Models Can Be Easily Distracted by Irrelevant Context. ICML 2023. https://proceedings.mlr.press/v202/shi23a.html
- Levy, M., Jacoby, A., and Goldberg, Y. (2024). Same Task, More Tokens: the Impact of Input Length on the Reasoning Performance of Large Language Models. ACL 2024. https://aclanthology.org/2024.acl-long.818/
- Sclar, M., Choi, Y., Tsvetkov, Y., and Suhr, A. (2024). Quantifying Language Models' Sensitivity to Spurious Features in Prompt Design. ICLR 2024. https://proceedings.iclr.cc/paper_files/paper/2024/hash/6c0e99d736da621403018ca7b32b1a4d-Abstract-Conference.html

## Usage

```sh
npm install
dotnet tool restore
npm run build:runner     # builds dotnet/EvalRunner, required for every eval run

npm test                 # compile + unit tests
npm run test:dotnet      # EF Core fixture tests

npm run eval -- --models mock                # cheap harness check
npm run eval -- --models haiku-5.5 --trials 3
npm run eval                                  # all configured models
npm run eval:trigger -- --models haiku-5.5 --trials 3   # description discovery eval (evals/trigger.json)
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

Every id in `targets` is ablated against this case. A case should fail without its target line and pass with it, so choose prompts where a model would not do the right thing unprompted. Models that already follow a rule unprompted produce `delta 0`, which is the signal that the line is not earning its place for that model. A `guard: true` case runs with the full skill only and reports a separate pass rate, so a regression check never dilutes a line's delta.

## How outputs are graded

A trial passes only if every check passes. Checks run from cheapest and most objective to most subjective:

| Tier | Check | Used for |
| --- | --- | --- |
| 1 | **Compiles.** Roslyn compiles the code block against the fixtures. | Every case. Catches uncompilable output that form checks accept, such as a tuple literal in an expression tree or a result variable named like the query's range variable. |
| 2 | **Runs.** Each public method is invoked on seeded in-memory SQLite. An exception fails the trial. | Every case. The non-translatable helper `TitleRules.HasPrefix` throws at runtime when used in a predicate. |
| 3 | **Executed SQL.** An EF command interceptor records every command. Cases assert with `sqlMust`, `sqlMustNot` and `minCommands`. | `translatable` (a `WHERE` and `LIKE` must reach the database), `values-only` (only the projected columns are selected), `split-query` (3 commands, not 1 join), grouping and ordering. |
| 4 | **Syntax facts from the semantic model.** Roslyn reports operators bound to `System.Linq.Queryable`, whether an awaited `IQueryable` is a local or an inline expression, and declared identifiers. | `keyword-syntax`, `separate-spec`. Replaced regexes that rejected correct code (a post-`ToList` `.Select` on a `List`, a newline before `.ToListAsync`). |
| 5 | **Regex** on the code block only (prose is ignored). | Remaining form checks, for example `ToListAsync(cancellationToken)`. |
| 6 | **LLM judge** for identifier names. | `meaningful-names` only. See below. |

**Why executed SQL rather than `ToQueryString()`.** `ToQueryString()` needs the `IQueryable` in hand, but the skill's own `separate-spec` rule and normal code end by executing the query and returning results, so there is nothing to call it on. The command interceptor sees the SQL for any shape of method. The runner also records `ToQueryString()` for any method that returns an `IQueryable` (`queryStrings` in the result), so both views exist. Assertions are on SQL properties (a `WHERE`, `GROUP BY`, which columns are selected, how many commands), not on exact text, so equivalent code written in method or keyword syntax passes.

**Why an LLM judge for names only.** Everything above has an executable oracle, which is deterministic, free to repeat and not subject to judge bias. Identifier quality does not: "meaningful words" is a language judgement, and a regex list of banned abbreviations misses new ones. The judge is kept narrow so its known weaknesses cannot dominate the result (Zheng et al., 2023, found position and verbosity bias and imperfect agreement; Panickssery et al., 2024, found models favour their own outputs):

- Roslyn extracts the declared identifiers. The judge sees only those short strings, never the model's code or answer, so there is no answer to favour and no second candidate to position-bias.
- Each unique identifier is labelled once with a fixed rubric, and the verdict is cached in `evals/name-verdicts.json` keyed by rubric hash and judge model. Re-grading is deterministic. Identifiers with no verdict, and single letters, fail closed.
- The rubric mirrors the skill rule ("whole ordinary English words"). A first draft added a "generic placeholder" clause the skill never states, and four judge models disagreed on 7 of 24 ordinary words; after the fix they agree on 30 of 31.
- It is calibrated: `npm run eval -- ...` is not trusted until `node dist/src/cli.js calibrate-names --judge <id>` agrees with `evals/name-labels.json` (59 labelled identifiers; all four judges agree on 59/59). The labels were written by the author of this repository and should be reviewed by a person; they cover clear cases, so they show the judge follows the rubric, not that it handles every borderline name.

**The graders are tested.** `evals/references.json` holds a correct solution and one or more deliberately wrong solutions for every case. `test/references.test.ts` fails if a grader rejects a correct reference or accepts a wrong one. This is what caught the grader errors listed above.

**Safety.** The runner executes model-generated C#. It compiles in a separate process, rejects code that references `System.IO`, `System.Net`, `System.Diagnostics`, `System.Reflection`, `Environment` or similar APIs before loading it, uses only in-memory SQLite and a 20 second timeout per method. This is defence in depth, not a sandbox; do not point it at untrusted prompts.

## Known limits

- Small trial counts are noisy. Use `--trials 5` or more before deleting a line, and treat a single-trial run as a smoke test. Cells with 10 to 30 trials are noted in `PROGRESS.md`.
- The fixtures are one small blog schema. Cases state the schema in the prompt because the harness has no repository; the skill itself contains no schema.
- The judge's calibration set is small and was labelled by the repository author.
- `with` runs are shared across lines of the same case, so token totals count each unique call once.

## References for grading design

- Zheng, L., Chiang, W.-L., Sheng, Y., et al. (2023). Judging LLM-as-a-Judge with MT-Bench and Chatbot Arena. NeurIPS 2023 Datasets and Benchmarks. https://proceedings.neurips.cc/paper_files/paper/2023/hash/91f18a1287b398d378ef22505bf41832-Abstract-Datasets_and_Benchmarks.html
- Panickssery, A., Bowman, S. R., and Feng, S. (2024). LLM Evaluators Recognize and Favor Their Own Generations. NeurIPS 2024. https://proceedings.neurips.cc/paper_files/paper/2024/file/7f1f0218e45f5414c79c0679633e47bc-Paper-Conference.pdf
