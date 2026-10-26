---
name: ef-linq
description: Write Entity Framework Core LINQ queries.
---
Rules for EF Core LINQ queries:
- [no-tracking] Use `AsNoTracking()` on read-only queries.
- [project] Project to only the needed columns with `Select(...)` instead of loading whole entities.
- [translatable] Keep `Where` predicates translatable to SQL: no custom C# methods, and never call `ToList()` before filtering.
- [async] Use async terminals such as `ToListAsync(cancellationToken)` and pass the `CancellationToken`.
- [split-query] Use `AsSplitQuery()` when `Include`-ing more than one collection.
