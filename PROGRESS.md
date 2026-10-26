# Skill progress log

Running log of skill changes against Michael's LINQ opinions. Each entry matches a git commit.
Evaluation: `npm run eval -- --models haiku-5.5 --lines <id> --trials 3`. Rate = fraction of trials passing the opinion's regex grader.

Opinions under evaluation:

| Line id | Opinion |
| --- | --- |
| `separate-spec` | Define the query in a `var`, execute it separately with `await` on a terminal method |
| `keyword-syntax` | Prefer query (keyword) syntax over extension methods |
| `meaningful-names` | Variable and lambda names are meaningful words, not letters or abbreviations |
| `values-only` | Project values from read-only queries, never entities; no `AsNoTracking()` |

## Log

| Step | Commit | Change | Result |
| --- | --- | --- | --- |
| 1 | 0b53e56 | Initial terse line per opinion; replaced old `no-tracking` line with `values-only` | haiku-5.5, 3 trials, without -> with: `separate-spec` 0.00 -> 1.00, `keyword-syntax` 0.00 -> 1.00, `meaningful-names` 0.33 -> 1.00, `values-only` 0.00 -> 0.33. Weak: `values-only` (model still writes `AsNoTracking()` on "read-only" prompts). |
| 2 | e0a91c3 | `values-only`: added the reason (nothing is tracked when values are projected) to the prohibition | haiku-5.5, 5 trials: `values-only` 0.00 -> 0.60. Remaining failures were all grader false negatives (valid tuple projections `select (a, b)` not recognised). |
| 3 | (this commit) | Grader fix only: `read-only-values` accepts tuple projection. Skill text unchanged. | haiku-5.5, 5 trials: `values-only` 0.00 -> 1.00. |
| 4 | (this commit) | Confirmation run, no skill change. "with" is the full 8-line skill, so this also checks the lines do not interfere. | haiku-5.5, 5 trials, without -> with: `separate-spec` 0.00 -> 1.00, `keyword-syntax` 0.00 -> 1.00, `meaningful-names` 0.20 -> 1.00, `values-only` 0.00 -> 1.00. All load bearing. Tokens (estimated): 10355 in / 4877 out over 40 calls. |

## Caveats

- One model (haiku-5.5) so far. Run `npm run eval` for the other configured models before treating the lines as final.
- Graders are regex, so they measure form, not correctness. The tuple false negative in step 3 shows graders also need auditing.
- `logs/` is gitignored; this file and `runs/` are the tracked record.
