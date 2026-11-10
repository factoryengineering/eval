---
name: unit-testing
description: Opinions for writing xUnit tests against an Entity Framework Core DbContext backed by SQLite.
---
Rules for xUnit tests against an EF Core DbContext:
- Name each test `Given<State>_When<Action>_Then<Outcome>`, for example `GivenEmptyCart_WhenCheckingOut_ThenTotalIsZero`.
- Name test helper methods with a `Given`, `When` or `Then` prefix, and do not add an `Async` suffix.
- Create the `SqliteConnection` and `DbContext` in `IAsyncLifetime.InitializeAsync` and dispose them in `DisposeAsync`, never inside a test method.
- Seed a child entity by adding it to its parent's navigation collection, never on its own, because an orphan child violates the foreign key.
