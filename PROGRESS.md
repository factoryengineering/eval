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
