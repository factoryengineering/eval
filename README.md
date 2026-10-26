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
.devin/skills/ef-linq/SKILL.md   The skill under test, in the folder Devin loads project skills from. One rule per line: "- [id] text"
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

## Skill structure and why it is not classic RTCC

The skill targets a coding agent and must stay valid across projects and as schemas change. It records Entity Framework opinions only. That fixes which RTCC components can be load bearing, meaning removing them would flip an eval:

| Component | In the skill? | Where it lives and why |
| --- | --- | --- |
| **Task** | Yes | The frontmatter `description` is what the agent reads to decide whether to load the skill. It is the only text present before the skill is chosen, so it is load bearing by discovery. An evaluation of it (the agent picks the skill for EF query work and ignores it for unrelated work) is planned, not yet built. |
| **Constraints** | Yes | The `- [id]` rules. Each is ablated against a targeted case and must change pass rates (see `PROGRESS.md`). This is the whole body of the skill. |
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

### Known gap: rule ids ship to the agent

The `[id]` tags let the harness ablate lines, but the agent loads the file as written, so it also sees the ids. They are not load bearing text. Options are a sidecar mapping from id to line text, or a build step that strips them into the deployed file. This is not yet done.

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
