# Unit-testing skill: evaluation-driven log

Process rules (from the ef-linq certification notes): one iteration is one commit, one `logs/` entry and one section here, written together. Nothing is deleted from `logs/` or `runs/`; discarded runs are archived and marked. Spec changes made after seeing results are recorded as such.

Judge model: `terra` (GPT-6.1 Terra), `judgeOnly`, never a model under test. Models under test: haiku-5.5, sonnet-5.5, gpt-6-luna-low, swe-2-medium, swe-2-flash-medium.

## Iteration 0 - harness (no skill yet)

- Harness is per-skill (`--config`, `home`), shared with ef-linq.
- Oracles in `dotnet/EvalRunner`: compile generated xUnit tests against the real `BlogContext` plus a subject, run Fact/Theory/IClassFixture/IAsyncLifetime in-process, then re-run against source **mutants** of the subject. A test suite passes only if it passes on the original and kills every mutant.
- Roslyn facts for structure rules: test names, helper names (interface implementations excluded), discard `using` declarations, connections opened inside test methods.
- Subject `evals/subjects/BlogQueries.cs`, two cases (`popular-titles`, `total-views-per-blog`), three mutants each.
- References (`evals/references.json`): one good and seven bad per case; `test/references.test.ts` requires the good to pass and each bad to fail **for its named reason**. This caught two of my own errors: interface methods counted as helpers (grader), and a bad reference that failed for the wrong rule (reference).

## Iteration 1 - baseline without a skill (3 trials per cell)

Before: the first smoke runs (1-2 trials) failed to compile because the prompt did not say which EF provider exists (haiku chose InMemory) and did not describe entity properties (haiku invented one). Those are prompt gaps, not model behaviour, so the prompts were changed to state the environment and entity shapes (commit 857b767). This is a spec change made after seeing results; the smoke runs were discarded (not part of the baseline) and the baseline below was run after the change.

Result (`runs/baseline-2026-11-10T03-31-36-354Z.json`). Fraction of trials failing each check, no skill:

| model | popular-titles | total-views-per-blog |
| --- | --- | --- |
| haiku-5.5 | given-when-then 1.00, gherkin-helpers 1.00 | given-when-then 1.00, gherkin-helpers 0.67 |
| sonnet-5.5 | given-when-then 1.00, gherkin-helpers 1.00 | given-when-then 1.00, gherkin-helpers 1.00 |
| gpt-6-luna | test failed 0.67, given-when-then 0.33 | given-when-then 1.00, no-connection-in-tests 0.67 |
| swe-2 | given-when-then 0.67, gherkin-helpers 0.67, test failed 0.33 | given-when-then 1.00, gherkin-helpers 1.00 |
| swe-2-flash | test failed 0.67, given-when-then 0.33, gherkin-helpers 0.33 | test failed 0.33, given-when-then 0.33, gherkin-helpers 0.33, compile 0.33 |

Findings:
- `given-when-then` and `gherkin-helpers` fail almost everywhere without a skill. They can be load-bearing.
- `no-discard-usings` never fired (0 of 30). Models do not write `using var _`, so a rule for it cannot show a delta. Candidate dropped unless a case that provokes it is found.
- `no-connection-in-tests` fired only for gpt-6-luna (0.67 on one case). Weak candidate; needs a case that makes inline setup more tempting.
- **No mutant survived in any trial.** When the prompt states the behaviour, models that compile and pass also write boundary and ordering tests. The mutation oracle works (references prove it) but does not discriminate at baseline with these prompts. It stays as a guard: a rule must not lower the kill rate.
- "test failed" is mostly real model error: seeding `Post` rows with no parent `Blog` (foreign key violation in `DbUpdateException`) and wrong expectations. Not an oracle fault.

## Iteration 2 - trial count and regression monitors (7 trials per case, 14 pooled per model)

Decision (by the repository owner): checks that models already pass are **regression monitors, not skill lines**. We measure their failure rate and add a skill line only if failure becomes significant. This keeps every line in the skill load-bearing and avoids rules that cannot show a delta.

Significance test: one-sided binomial against an assumed noise ceiling of 0.05 failures, alpha 0.05. Sample size: smallest n that detects a true failure rate of 0.3 (the ablation `minDelta`) with power 0.8. That is n = 14 (n = 13 gives 0.7975, just short). With two cases, 7 trials per case gives 14 per model. At n = 14 a model signals at 3 or more failing trials; 2 never signals. Power to detect a true 0.2 failure rate is only about 0.55, so a 0.2 rise is **not** reliably visible at this size; that is a stated limit, not a hidden one. (`src/monitor.ts`, `test/monitor.test.ts`.)

Result (`runs/baseline-2026-11-10T03-57-10-563Z.json`, no skill, failing trials out of 14 per model):

| check | haiku-5.5 | sonnet-5.5 | gpt-6-luna | swe-2 | swe-2-flash |
| --- | --- | --- | --- | --- | --- |
| rule given-when-then | 14 SIGNAL | 14 SIGNAL | 11 SIGNAL | 13 SIGNAL | 10 SIGNAL |
| rule gherkin-helpers | 12 SIGNAL | 11 SIGNAL | 0 | 13 SIGNAL | 10 SIGNAL |
| rule no-connection-in-tests | 0 | 0 | 10 SIGNAL | 0 | 0 |
| rule no-discard-usings | 0 | 0 | 0 | 1 | 0 |
| mutant survived | 0 | 0 | 0 | 0 | 0 |
| test failed | 0 | 0 | 3 SIGNAL | 0 | 1 |
| compile | 0 | 0 | 0 | 1 | 1 |

Reading it:
- `given-when-then` and `gherkin-helpers` are signalled on 5 of 5 and 4 of 5 models: skill line candidates.
- `no-discard-usings` and `mutant survived` stay quiet on every model: regression monitors only.
- `no-connection-in-tests` signals on one model (gpt-6-luna, 10 of 14; it was 0.67 on one case at 3 trials). By the rule above this is a signal, but on 1 of 5 models. Not acted on yet; needs an owner decision (see below).
- `test failed` on gpt-6-luna (3 of 14) is correctness, not style (see Iteration 1: posts seeded without a parent blog).
- `forbidden api` appeared (swe-2-flash, 1 of 7 per case) but was not in the monitored check list; it is now (applies from the next run).
