---
name: ef-linq
description: Write Entity Framework Core LINQ queries.
---
Rules for EF Core LINQ queries:
- [translatable] Filter only in the database: never call user-defined C# helper methods in a query, and never filter results after `ToList()` or `ToListAsync()`. Use built-in translatable members such as `StartsWith`.
- [async] Use async terminals such as `ToListAsync(cancellationToken)` and pass the `CancellationToken`.
- [split-query] Use `AsSplitQuery()` when `Include`-ing more than one collection.
- [separate-spec] Assign the query to a `var`, then execute it in a separate statement with `await`.
- [keyword-syntax] Prefer query syntax (`from ... select`) over extension-method syntax.
- [meaningful-names] Use meaningful words for variable and lambda parameter names.
- [values-only] Read-only queries project values, never entities, so nothing is tracked: never write `AsNoTracking()`.
