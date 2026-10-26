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

## Caveats

- Token counts are estimated (chars / 4) for `devin-cli` models; consistent between runs, so deltas are valid.
- Graders are regex, so they measure form, not correctness. Case defects (Iterations 5-7) were found by reading raw outputs. A compile-and-`ToQueryString()` grader is the planned next step.
- `translatable` cannot detect a client-side filter written in a separate statement without a banned token; it relies on the `HasPrefix(` ban.
- 5 trials per cell; counts are 0/5 to 5/5, so deltas of 0.2 are noise.
