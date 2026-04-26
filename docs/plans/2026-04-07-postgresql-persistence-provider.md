# PostgreSQL Persistence Provider — Design & Implementation Plan v2

**Issue:** [#236](https://github.com/nightBaker/fleans/issues/236)  
**Date:** 2026-04-07  
**Status:** Implemented (Phases 1–5)

## Problem

Fleans launched with SQLite as the only persistence provider. SQLite is convenient for local dev but unsuitable for production: it doesn't support concurrent writers across processes, has no connection pooling, and can't run in a containerised or distributed environment.

## Design Decisions

### Provider Selection

A single `Persistence:Provider` config key (values `Sqlite` | `Postgres`, case-insensitive, default `Sqlite`) selects the active provider at startup. Invalid values throw so misconfiguration fails fast.

### `IModelCustomizer` pattern

Each provider ships a `RelationalModelCustomizer` subclass. Registering it via `ReplaceService<IModelCustomizer, T>()` ensures EF Core uses a distinct model-cache key per provider — prevents cross-provider model bleed in processes that register both (e.g. integration tests). The customizer is also the extension point for provider-specific column type overrides.

### DateTimeOffset storage

SQLite stores `DateTimeOffset` as a string (via a value converter in `SqliteModelCustomizer`) because SQLite has no native date type and its `ORDER BY` behaviour on ISO-8601 strings is stable.

PostgreSQL stores `DateTimeOffset` as native `timestamptz` — no converter needed. The shared model defines the columns without converters; the SQLite customizer applies the converter override.

### JSON columns

`text` in v1 for both providers (matches existing SQLite layout). Migration to PostgreSQL `jsonb` for better query performance is deferred.

### Migration strategy

| Provider | Schema strategy | Who applies |
|---|---|---|
| SQLite | `EnsureCreated()` | `Fleans.Api`, `Fleans.Web`, `Fleans.Mcp` each on startup |
| PostgreSQL | `MigrateAsync()` | `Fleans.Api` only; Web and Mcp wait via Aspire `WaitFor` |

### Connection pooling

PostgreSQL uses `NpgsqlDataSource` (Npgsql 8+ recommended pattern), registered as a singleton in DI. The data source owns connection pooling and is disposed cleanly on host shutdown.

### Read replica

`ConnectionStrings:fleans-query` activates a second `NpgsqlDataSource` for the query `DbContext`. The read replica must be provisioned separately. Aspire v1 provisions only the primary; replica wiring is tracked in issue [#170](https://github.com/nightBaker/fleans/issues/170).

## Implementation Phases

### Phase 1 — Shared persistence refactor
- Extract `FleanModelConfiguration` (shared model config, no provider specifics)
- Introduce `IModelCustomizer` / `RelationalModelCustomizer` extension pattern
- Create `Fleans.Persistence.Sqlite` project with `SqliteModelCustomizer` and `AddSqlitePersistence()` 
- Add `DateTimeOffset` string converter in SQLite customizer
- Migrate all hosts to use `AddSqlitePersistence()`

### Phase 2 — DateTimeOffset/ETag migrations
- Normalise `WorkflowInstanceState` columns (`CreatedAt`, `ExecutionStartedAt`, `CompletedAt`, `ETag`)
- Generate SQLite migration
- All existing tests continue to pass

### Phase 3 — `Fleans.Persistence.PostgreSql` provider
- Create `Fleans.Persistence.PostgreSql` class library (net10.0)
- `PostgresModelCustomizer` — v1: no overrides (timestamptz native, text JSON columns)
- `AddPostgresPersistence(connectionString, queryConnectionString?)` extension — builds `NpgsqlDataSource`, calls `AddEfCorePersistence`, applies customizer
- Generate initial PostgreSQL EF Core migrations
- Update all hosts to read `Persistence:Provider` and branch between SQLite / Postgres
- Update Aspire to auto-provision a Postgres container when `FLEANS_PERSISTENCE_PROVIDER=Postgres`

### Phase 4 — Testcontainers integration tests
- Add `Testcontainers.PostgreSql` to `Fleans.Persistence.Tests`
- `PostgresContainerFixture` + `AssemblyInit` manage a single shared container per test assembly
- `EfCoreEventStorePostgresTests` — mirrors `EfCoreEventStoreTests` against PostgreSQL
- `EfCoreProcessDefinitionRepositoryPostgresTests` — mirrors key repository tests
- All PostgreSQL test classes carry `[TestCategory("Postgres")]`
- Tests skip (Inconclusive) when `FLEANS_PG_TESTS` env var is not set, keeping default `dotnet test` fast

### Phase 5 — Documentation and CI
- `website/src/content/docs/reference/persistence.md` — persistence providers reference (provider selection, connection strings, migration behaviour, Aspire auto-provisioning)
- This design doc captures the source-of-truth architecture
- `.github/workflows/postgres-tests.yml` — dedicated CI job that sets `FLEANS_PG_TESTS=1` and runs `dotnet test --filter "TestCategory=Postgres"`; Testcontainers manages the container lifecycle from within the tests

## Limitations (v1)

| Feature | Status |
|---|---|
| JSON columns as `jsonb` | Deferred |
| Read-replica Aspire wiring | Deferred (#170) |
| Nested transaction support | Deferred (#307) |
| Per-silo grain placement filtering | Deferred (structural split only today) |

## Key Files

| File | Role |
|---|---|
| `Fleans.Persistence/FleanModelConfiguration.cs` | Shared EF model config |
| `Fleans.Persistence.Sqlite/SqliteModelCustomizer.cs` | SQLite-specific model overrides |
| `Fleans.Persistence.Sqlite/SqlitePersistenceDependencyInjection.cs` | `AddSqlitePersistence()` |
| `Fleans.Persistence.PostgreSql/PostgresModelCustomizer.cs` | PostgreSQL model customizer (v1 no-op) |
| `Fleans.Persistence.PostgreSql/PostgresPersistenceDependencyInjection.cs` | `AddPostgresPersistence()` |
| `Fleans.Persistence.PostgreSql/Migrations/` | EF Core migrations for PostgreSQL |
| `Fleans.Persistence.Tests/PostgresContainerFixture.cs` | Testcontainers shared fixture |
| `Fleans.Persistence.Tests/*PostgresTests.cs` | Provider integration tests |
| `.github/workflows/postgres-tests.yml` | Dedicated PostgreSQL CI job |
