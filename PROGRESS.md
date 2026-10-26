# Skill iteration log (EDD: Red / Green / Refactor)

Skill under test: `.devin/skills/ef-linq/SKILL.md` (moved from `skills/ef-linq/` after Iteration 8 so the Devin coding agent loads it; text unchanged, hash 74acfcbbdcff). Each iteration records baseline, hypothesis, change, measured result and reasoning.
Rates are "without the line -> with the full skill" pass rates. Command: `npm run eval -- --models <id> --lines <ids> --trials 5`.
Evidence: `logs/evals.jsonl` (summaries), `logs/changes.jsonl` + `logs/snapshots/<hash>.md` (skill text per change), `runs/<runId>.json` (raw outputs).
Model ids: `haiku-5.5`, `sonnet-5.5`, `gpt-6-luna` (all mid-tier or lower, via `devin-cli`; token counts are estimates).

A line is **load bearing** on a model when `with - without >= 0.3` (`eval.config.json: minDelta`).

## Phase A: original five lines (skill hash d2151feef2ee)

### Iteration 0 - RED: first harness run (commit 89a4f65)
- **Baseline:** none. 5 lines: `no-tracking`, `project`, `translatable`, `async`, `split-query`.
- **Hypothesis:** every line changes model output.
- **Change:** none, first measurement. haiku-5.5, 1 trial.
- **Result:** `no-tracking` 0->1, `split-query` 0->1; `project`, `translatable`, `async` 1->1.
- **Reasoning:** three lines already followed unprompted, or the cases cannot tell. One trial is a smoke test only. Left open; resolved in Phase C.

### Iteration 1 - REFACTOR: scope pivot to four opinions (commit 0b53e56, hash 7140c62e564a)
- **Baseline:** Iteration 0 skill.
- **Hypothesis:** opinionated style rules (the author's LINQ preferences) are what models do not do unprompted, so they make better load-bearing lines than generic best practice.
- **Change:** added `separate-spec`, `keyword-syntax`, `meaningful-names`, `values-only`; `no-tracking` replaced by `values-only`. Added a case per opinion.
- **Result (haiku-5.5, 3 trials):** `separate-spec` 0.00->1.00, `keyword-syntax` 0.00->1.00, `meaningful-names` 0.33->1.00, `values-only` 0.00->0.33.
- **Reasoning:** the three style rules are clearly load bearing. `values-only` is weak: the model still writes `AsNoTracking()` on "read-only" prompts.

## Phase B: tightening `values-only`

### Iteration 2 - GREEN (partial): give the reason (commit e0a91c3, hash 35deadefb012)
- **Baseline:** `values-only` 0.00 -> 0.33.
- **Hypothesis:** a bare prohibition is ignored; stating why (nothing is tracked when values are projected) will lift it to at least 0.8.
- **Change:** added the reason to the `values-only` line.
- **Result (5 trials):** 0.00 -> 0.60.
- **Reasoning:** improved, short of target. Every remaining failure was a valid tuple projection (`select (a, b)`) the grader rejected, so the grader, not the skill, was the problem.

### Iteration 3 - REFACTOR: fix grader false negative (commit 36fffe0)
- **Baseline:** `values-only` 0.60 with the same skill text.
- **Hypothesis:** accepting tuple projections will raise the measured rate without any skill change.
- **Change:** grader regex only. Skill text unchanged.
- **Result (5 trials):** 0.00 -> 1.00.
- **Reasoning:** confirms the skill was already correct. Graders need auditing as much as prompts do.

### Iteration 4 - confirmation (commit 67b1112)
- **Result (haiku-5.5, 5 trials):** all four opinion lines load bearing: `separate-spec` 0->1, `keyword-syntax` 0->1, `meaningful-names` 0.2->1, `values-only` 0->1. The three carried-over lines were not re-run.
- **Gap found:** `project`, `translatable`, `async`, `split-query` still unmeasured at 5 trials. See Phase C.

## Phase C: eliminating dead weight (full 8-line skill)

### Iteration 5 - RED: full-skill baseline (hash 35deadefb012, run 2026-10-26T14-45-25-426Z)
- **Baseline:** none for the carried-over lines. haiku-5.5, 5 trials, all 8 lines.
- **Hypothesis:** all 8 lines are load bearing.
- **Result:** `project` 1.00->1.00, `translatable` 1.00->1.00, `async` 1.00->1.00 (delta 0, dead weight, 3 of 8 lines = 37.5%, violating the 90% load-bearing rule). `split-query` 0.00->0.40, `keyword-syntax` 0.00->0.80. Other four at 1.00.
- **Reasoning (from raw outputs):**
  - `split-query` failed because the case never required entities. Other lines (project values) steered the model to projections with no `Include`, so `AsSplitQuery` was irrelevant. Case defect, not skill defect.
  - `async` passed without the line because the grader only checked `ToListAsync(`, which models write unprompted. The line is about passing the token.
  - `project` duplicates `values-only`, which already says to project values.
  - `translatable` prompt (search by title) is naturally translatable, so there was no temptation to violate it.

### Iteration 6 - GREEN: harden the cases (run 2026-10-26T14-58-53-021Z)
- **Baseline:** `async` 1.00->1.00, `split-query` 0.00->0.40.
- **Hypothesis:** a case that demands entities for modification will make `split-query` testable; requiring `ToListAsync(cancellationToken)` will make `async` testable.
- **Change (cases only, skill unchanged):** `blog-with-posts-and-tags` now says callers modify and save the entities. `list-popular-posts` must match `ToListAsync(<...cancellationToken...>)` and targets only `async`.
- **Result (5 trials):** `async` 0.00->1.00, `split-query` 0.00->1.00 (both now load bearing).
- **Reasoning:** the lines were fine; the tests could not see them.

### Iteration 7 - RED then GREEN: `translatable` (hash c079ade92107, then 74acfcbbdcff)
- **Baseline:** `translatable` 0.00->0.00 after the first case rewrite.
- **7a hypothesis:** adding `ToListAsync` to the rule and naming `EF.Functions` fixes it. **Change:** deleted `project`, rewrote `translatable`. **Result:** 0.00->0.00. **Reasoning:** the case demanded slug validity, which cannot be written in SQL, so the helper was the only correct answer. The case was unfair.
- **7b hypothesis:** with a translatable predicate (title starts with prefix) and a tempting helper `TitleRules.HasPrefix`, the rule should work. **Change:** case rewritten. **Result:** 0.00->0.60. **Reasoning:** remaining failures used the helper in a client-side `Where` after a database prefilter.
- **7c hypothesis:** the rule must ban user-defined helpers and any post-materialization filter, and name a replacement. **Change:** `translatable` now reads "Filter only in the database: never call user-defined C# helper methods in a query, and never filter results after `ToList()` or `ToListAsync()`. Use built-in translatable members such as `StartsWith`." **Result (5 trials):** 0.00->1.00.
- **Net skill change in Phase C:** `project` deleted (redundant, delta 0); `translatable` reworded. 7 lines remain.

### Iteration 8 - final multi-model confirmation (hash 74acfcbbdcff)
Full 7-line skill, 5 trials per line, three models. Runs 2026-10-26T15-31-58 (sonnet), T15-33-23 (haiku), T15-34-07 (gpt-6-luna). Cells are without -> with.

| Line | haiku-5.5 | sonnet-5.5 | gpt-6-luna |
| --- | --- | --- | --- |
| `translatable` | 0.00 -> 1.00 | 1.00 -> 1.00 (not load bearing) | 0.00 -> 1.00 |
| `async` | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 |
| `split-query` | 0.20 -> 1.00 | 0.20 -> 1.00 | 0.00 -> 1.00 |
| `separate-spec` | 0.00 -> 1.00 | 0.40 -> 1.00 | 0.00 -> 1.00 |
| `keyword-syntax` | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 |
| `meaningful-names` | 0.20 -> 1.00 | 0.00 -> 1.00 | 1.00 -> 1.00 (not load bearing) |
| `values-only` | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.20 -> 1.00 |

- **Result:** every line passes 100% with the skill on every model (7 of 7 lines x 3 models). All 7 lines are load bearing on at least 2 of 3 models, so 100% of lines (above the 90% bar) earn their place. 19 of 21 line-model cells are load bearing.
- **Reasoning:** the two exceptions are model-specific (sonnet already filters in the database, luna already names variables well). Neither line is dead weight, because it is decisive on the other models.

### Iteration 9 - precedence probe and grader refactor (hash 74acfcbbdcff, skill unchanged)
- **Baseline:** Iteration 8 skill, all lines 5/5 with the skill.
- **Hypothesis:** `values-only` ("never entities") conflicts with workflows that must load a tracked entity, so the skill will break a legitimate write path. If true, a scoping line would be load bearing.
- **Change:** new case `load-entity-for-edit` (load a post, change `Title`, save; must use `SaveChangesAsync`, a tracked-load method and no `AsNoTracking`/`ExecuteUpdate`), targeting `values-only`. No skill change (RED probe).
- **Result (5 trials, without -> with, pooled with the read-only case):** haiku-5.5 0.50 -> 1.00, sonnet-5.5 0.50 -> 0.90, gpt-6-luna 1.00 -> 1.00. On the new case alone the skill passed 14/15 trials; every model without the skill passed 5/5.
- **Reasoning:** hypothesis rejected. The models read "read-only queries" as scoping `values-only` and load a tracked entity for writes. The one failure (sonnet) was a grader false positive: the model's prose said it "leaves out `AsNoTracking()`" and the regex matched prose. No precedence line is justified, so none was added (adding one would be dead weight).
- **Refactor:** `grade()` now checks only fenced code blocks when any are present (`src/grade.ts`, unit test added). Re-grading all stored trials from the Iteration 8 and later runs gave identical results except that one false positive, so earlier conclusions stand.

## Phase D: the skill description (discovery)

The frontmatter `description` is the only skill text the agent sees before deciding to load the skill, so it is a prompt component and needs its own evaluation. `npm run eval:trigger` shows a model a skill list (this skill plus 6 plausible neighbours such as `ef-migrations` and `sql-tuning`), gives it a user request, and asks which skills it would invoke. A trial passes when `ef-linq` is chosen exactly when it should be. Description variants are built from named clauses: `none` (name only), `baseline`, `full`, and `full` minus one clause. A clause is load bearing when removing it lowers the pass rate on the cases that target it by at least 0.3. Runs: `runs/trigger-*.json`, summaries in `logs/trigger.jsonl`.

### Iteration 10 - RED: the trigger eval was too easy (runs 2026-10-26T16-08 to T16-11, spec v1)
- **Baseline:** description "Write Entity Framework Core LINQ queries." 15 cases, 3 clauses (`scope`, `actions` = "Use when writing, reviewing, or refactoring them.", `exclusions`), 3 trials, 3 models.
- **Hypothesis:** the description changes which skill gets chosen, so each clause shows a delta.
- **Result:** `none` (name only) already scored 0.91 / 1.00 / 0.93 (haiku / sonnet / luna) overall. All clause deltas were below 0.3 (range -0.13 to 0.08).
- **Reasoning:** the name `ef-linq` plus distinct neighbour descriptions does nearly all the work, and the cases were not hard enough to need the description. Per-case failure counts showed only one case discriminated: LINQ over an in-memory list, which no neighbour claims. The evidence says to test near-miss negatives, not more positives.

### Iteration 11 - GREEN: scope and exclusions (spec v3; skill hash 4ae7fc1326d4)
- **Baseline:** spec v2 (22 cases adding LINQ-to-XML, DataTable, JSON, `FromSqlRaw`, `ExecuteSqlRawAsync`, Dapper and oblique positives), same three clauses. `actions` deltas were 0.07 / -0.07 / 0.00, so it was dead weight. `exec-sql` failed 7/9 under the baseline description and 0/9 under `full`; non-EF LINQ cases dropped from 6/9 failures to 1-2/9 once the scope clause was present.
- **Hypothesis:** dropping `actions` and keeping `scope` + `exclusions` keeps the gain with fewer words. Positives saturate even with no description, so they become untargeted recall guards and only near-miss negatives target a clause.
- **Change:** `description` is now "Opinions for LINQ queries against an Entity Framework Core DbContext. Not for migrations, model configuration, or raw SQL." (was "Write Entity Framework Core LINQ queries."). Spec: `actions` clause removed, positive cases untargeted.
- **Result (3 trials, 66 trials per variant per model):**

| Model | `none` | `baseline` | `full` | `scope` without -> with | `exclusions` without -> with |
| --- | --- | --- | --- | --- | --- |
| haiku-5.5 | 0.85 | 0.85 | 0.97 | 0.58 -> 0.92 (load bearing) | 0.67 -> 1.00 (load bearing) |
| sonnet-5.5 | 0.92 | 0.91 | 0.98 | 0.58 -> 1.00 (load bearing) | 0.44 -> 0.89 (load bearing) |
| gpt-6-luna | 0.77 | 0.86 | 0.91 | 0.50 -> 0.67 (0.17, not) | 0.78 -> 0.78 (0.00, not) |

- **Reasoning:** the description gains +0.06 to +0.14 overall over name-only, and negatives improve most (haiku 0.77 -> 0.95, sonnet 0.87 -> 0.97, luna 0.62 -> 0.85). Both remaining clauses are load bearing on 2 of 3 models. Recall is about 1.00 in every variant, so the description does not help recall at all; the name does that.

### Iteration 11b - add SWE-1.7 and SWE-2 to the trigger suite
Same spec and description (hash 4ae7fc1326d4), 3 trials. SWE models: `swe-1-7-medium` and `swe-2-medium`.

| Model | `none` | `baseline` | `full` | `scope` without -> with | `exclusions` without -> with |
| --- | --- | --- | --- | --- | --- |
| swe-1.7 | 0.76 | 0.79 | 0.89 | 0.25 -> 0.42 (0.17, not) | 0.67 -> 1.00 (load bearing) |
| swe-2 | 0.77 | 0.91 | 0.97 | 0.25 -> 0.92 (load bearing) | 0.44 -> 0.89 (load bearing) |

Across all 5 models `scope` is load bearing on 3 (haiku, sonnet, swe-2) and `exclusions` on 4 (all but luna). `full` beats name-only on every model (+0.06 to +0.20).

## Phase E: neutral-directory re-run and five-model confirmation

### Iteration 12 - REFACTOR: guard cases; GREEN: confirmation on 5 models (skill hash 4ae7fc1326d4, skill text unchanged)
- **Baseline:** Iteration 9 ran after the skill moved, from the project directory, so the model could see the one-line description of `ef-linq`. A first neutral-directory re-run of all 7 lines on 5 models (runs 2026-10-26T16-3x to T16-4x) also exposed a harness flaw: the `load-entity-for-edit` probe targeted `values-only`, so its always-passing results were pooled into that line and diluted its delta (haiku 0.70 -> 1.00, delta 0.30; swe-2 1.00 -> 1.00).
- **Hypothesis:** a regression check is not an ablation. Run it with the full skill only and report it separately, and the `values-only` delta reflects only the case that tests it.
- **Change (harness only):** `EvalCase.guard`. Guard cases run with the full skill, skip line deltas, and appear as a `guards` section in the summary (unit test added). `load-entity-for-edit` is now a guard. SWE-1.7 and SWE-2 added to `eval.config.json`.
- **Result (5 trials, neutral directory, without -> with):**

| Line | haiku-5.5 | sonnet-5.5 | gpt-6-luna | swe-1.7 | swe-2 |
| --- | --- | --- | --- | --- | --- |
| `translatable` | 0.00 -> 1.00 | 0.60 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 |
| `async` | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 |
| `split-query` | 0.00 -> 1.00 | 0.60 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 |
| `separate-spec` | 0.00 -> 1.00 | 1.00 -> 1.00 (not) | 0.00 -> 1.00 | 0.20 -> 1.00 | 0.20 -> 1.00 |
| `keyword-syntax` | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 | 0.00 -> 1.00 |
| `meaningful-names` | 0.20 -> 1.00 | 0.00 -> 1.00 | 1.00 -> 1.00 (not) | 0.00 -> 1.00 | 0.00 -> 1.00 |
| `values-only` | 0.40 -> 1.00 | 0.00 -> 1.00 | 0.60 -> 1.00 | 1.00 -> 1.00 (not) | 0.80 -> 1.00 (0.20, not) |
| guard `load-entity-for-edit` | 5/5 | 5/5 | 5/5 | 5/5 | 5/5 |

- **Reasoning:** every line passes 100% with the skill on every model, and the Iteration 9 conclusion (`values-only` does not break tracked-entity writes) holds from a neutral directory, now on 5 models. 31 of 35 line-model cells are load bearing; every line is load bearing on at least 3 of 5 models (`values-only` is weakest: haiku, sonnet and luna, with the SWE models already avoiding `AsNoTracking()` unprompted). The first neutral re-run is kept in `runs/` as superseded evidence.

### Iteration 13 - REFACTOR: remove ids from the shipped skill (skill hash 6ecabd3f5378, behavior unchanged)
- **Baseline:** the deployed `SKILL.md` carried `[id]` tags that the agent would read as text. They are not instructions and no eval covers them (dead weight, against the load-bearing rule).
- **Hypothesis:** moving id -> text to a sidecar (`evals/skill-lines.json`) and tagging in memory removes the tags from the shipped file without changing what any model sees during evaluation.
- **Change:** `annotate()` in `src/skill.ts` (3 unit tests: tagging, round trip, loud failure on drift); `SKILL.md` is now untagged; the run hash is computed on the rendered text.
- **Result:** the new `SKILL.md` is byte-identical (SHA-256 prefix `6ecabd3f5378`) to the rendered text every model received in Iterations 8-12, so those results apply unchanged and no re-run was needed. The CLI path was checked with a mock run (discarded). 15 unit tests pass.
- **Reasoning:** an identical model input cannot change a pass rate, so a byte comparison is stronger evidence than a re-run. Hashes in earlier logs (e.g. 74acfcbbdcff, 4ae7fc1326d4) were computed over the tagged text; 6ecabd3f5378 is the hash of the shipped text.

## Caveats

- Trigger eval: the spec was revised twice after seeing results (Iterations 10 and 11), so the final numbers are tuned to the final cases and likely optimistic. Cells are 18-39 trials. Distractor skills are synthetic and the model's own agent harness lists other skills too, though `devin-cli` now runs from an empty directory so the real `ef-linq` skill is not visible to the model under test.
- Iteration 9 ran after the skill moved into `.devin/skills/`, from the project directory, so the model could see the one-line description of `ef-linq` in its own skill list (not the rules). The rule-ablation evidence in Iterations 1-8 predates the move and is unaffected. Iteration 12 re-ran it from a neutral directory with the same conclusion.
- Token counts are estimated (chars / 4) for `devin-cli` models; consistent between runs, so deltas are valid.
- Graders are regex, so they measure form, not correctness. Case defects (Iterations 5-7) were found by reading raw outputs. A compile-and-`ToQueryString()` grader is the planned next step.
- `translatable` cannot detect a client-side filter written in a separate statement without a banned token; it relies on the `HasPrefix(` ban.
- 5 trials per cell; counts are 0/5 to 5/5, so deltas of 0.2 are noise.
