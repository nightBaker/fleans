# EF Core IGrainStorage for ActivityInstanceState (Relational Mapping)

## Context

Grain state is currently persisted via `InMemoryGrainStorage` (a `ConcurrentDictionary`). We want to prove that `ActivityInstanceState` can be mapped relationally via EF Core through Orleans `IGrainStorage`. This validates the path toward real database persistence while using SQLite to verify the relational schema. Scope: `ActivityInstanceState` only.

## Key Design Decisions

- **New project `Fleans.Persistence`** — provider-agnostic EF Core code (DbContext, IGrainStorage). References `Microsoft.EntityFrameworkCore.Relational`, not a specific provider. Aligns with MEMORY.md architecture (`Fleans.Persistence` + future `Fleans.Persistence.PostgreSql`). The InMemory project continues to provide the dictionary-based storage for `workflowInstances`.
- **Explicit `Guid Id` on `ActivityInstanceState`** — no shadow properties. The grain already has a Guid key; storing it on the state is natural. `internal set` with `InternalsVisibleTo` for the persistence assembly.
- **`string? ETag` on `ActivityInstanceState`** — explicit property with `internal set`, `HasMaxLength(64)`. Manual ETag checks in the storage provider (no EF Core `IsConcurrencyToken` — avoids dual concurrency mechanisms).
- **SQLite** — validates column types, constraints, owned entities. Uses in-memory mode (`DataSource=:memory:`) with a persistent connection so no files on disk.
- **Update strategy: `SetValues` + `Reference.CurrentValue`** — keep the tracked entity from `FindAsync`, copy scalar properties via `Entry.CurrentValues.SetValues(source)`, and assign the owned `ErrorState` via `Reference.CurrentValue`. This correctly handles all owned entity transitions (null->set, set->different, set->null) without needing detach/attach or manual per-property copying. Pure `db.Update()` was rejected because it doesn't generate NULL for optional owned entity columns when the navigation is null.

## Database Schema

```
ActivityInstances table:
├── Id                  Guid            PK (explicit property on domain model)
├── ETag                string?(64)     (explicit property on domain model)
├── ActivityId          string?(256)
├── ActivityType        string?(256)
├── IsExecuting         bool
├── IsCompleted         bool
├── VariablesId         Guid
├── ErrorCode           int?            (owned: ActivityErrorState.Code)
├── ErrorMessage        string?(2000)   (owned: ActivityErrorState.Message)
├── CreatedAt           DateTimeOffset?
├── ExecutionStartedAt  DateTimeOffset?
├── CompletedAt         DateTimeOffset?
```

## Files

| File | Action | Purpose |
|------|--------|---------|
| `Fleans.Domain/States/ActivityInstanceState.cs` | Modify | Add `Id`, `ETag` properties (`internal set`) |
| `Fleans.Domain/ActivityInstance.cs` | Modify | Add private ctor to `ActivityErrorState` |
| `Fleans.Domain/Fleans.Domain.csproj` | Modify | Add `InternalsVisibleTo` for persistence assemblies |
| `Fleans.Persistence/Fleans.Persistence.csproj` | Create | New project (EF Core Relational + Orleans SDK) |
| `Fleans.Persistence/GrainStateDbContext.cs` | Create | EF Core DbContext with owned entity mapping |
| `Fleans.Persistence/EfCoreActivityInstanceGrainStorage.cs` | Create | IGrainStorage impl |
| `Fleans.Persistence/DependencyInjection.cs` | Create | DI registration (`AddEfCorePersistence`) |
| `Fleans.Persistence.Tests/Fleans.Persistence.Tests.csproj` | Create | Test project |
| `Fleans.Persistence.Tests/EfCoreActivityInstanceGrainStorageTests.cs` | Create | 18 storage tests |
| `Fleans.Persistence.InMemory/DependencyInjection.cs` | Modify | Remove `"activityInstances"` registration |
| `Fleans.Api/Program.cs` | Modify | Wire up EF Core persistence (SQLite in-memory) |
| `Fleans.Api/Fleans.Api.csproj` | Modify | Add references to Persistence + SQLite provider |

## Test Coverage (18 tests)

- Round-trip (write + read all properties)
- ETag concurrency (stale ETag throws on write and clear)
- First write without ETag succeeds
- Write with stale ETag to non-existent key throws
- ErrorState round-trip (null + non-null)
- ErrorState transitions: add, clear, overwrite
- Clear removes state, subsequent read returns default
- Clear non-existent grain is no-op
- Read non-existent key returns default
- Different grain IDs isolated
- Timestamp preservation
- Update overwrites correctly
- Write-clear-write re-creates same grain ID

## Verification

1. `dotnet build` from `src/Fleans/` — 0 errors
2. `dotnet test` from `src/Fleans/` — 167 tests pass (18 new persistence + 149 existing)
