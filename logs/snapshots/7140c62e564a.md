---
name: ef-linq
description: Write Entity Framework Core LINQ queries.
---
Rules for EF Core LINQ queries:
- [project] Project to only the needed columns with `Select(...)` instead of loading whole entities.
- [translatable] Keep `Where` predicates translatable to SQL: no custom C# methods, and never call `ToList()` before filtering.
- [async] Use async terminals such as `ToListAsync(cancellationToken)` and pass the `CancellationToken`.
- [split-query] Use `AsSplitQuery()` when `Include`-ing more than one collection.
- [separate-spec] Assign the query to a `var`, then execute it in a separate statement with `await`.
- [keyword-syntax] Prefer query syntax (`from ... select`) over extension-method syntax.
- [meaningful-names] Use meaningful words for variable and lambda parameter names.
- [values-only] Do not use `AsNoTracking()`; project values from read-only queries.
