# Persistence providers

Two providers: **SQLite** (default, local dev) and **PostgreSQL** (production / load testing). Selected via configuration — no code changes needed.

## Configuration

- **Config key:** `Persistence:Provider` (values: `Sqlite` | `Postgres`, case-insensitive, default `Sqlite`). `AddFleansPersistence` throws `ArgumentException` on any other value (typos, empty, whitespace) so misconfigured deployments fail fast — mirrors the validation pattern in `FleanStreamingExtensions.cs:26`.
- **Aspire:** set `FLEANS_PERSISTENCE_PROVIDER=Postgres` before launch to auto-provision a Postgres container.
- **Connection strings:** SQLite uses `FLEANS_SQLITE_CONNECTION` / `FLEANS_QUERY_CONNECTION`. PostgreSQL uses `ConnectionStrings:fleans` (required) and `ConnectionStrings:fleans-query` (optional read replica).
- **Migration strategy:** SQLite uses `EnsureCreated()`. PostgreSQL uses `MigrateAsync()` (migrations applied automatically by `Fleans.Api` on startup).
- **Migrations live per-provider:** `Fleans.Persistence.Sqlite/Migrations/Command/` and `Fleans.Persistence.PostgreSql/Migrations/Command/`. Only command-context migrations are maintained (command and query share the same database).

## Provider packages

`Fleans.Persistence.Sqlite` and `Fleans.Persistence.PostgreSql` — each registers a `RelationalModelCustomizer` subclass via `ReplaceService<IModelCustomizer>` for provider-specific model tweaks (e.g., SQLite stores `DateTimeOffset` as string; PostgreSQL uses native `timestamptz`).

## Adding a new provider

1. Create a new `Fleans.Persistence.<Provider>` project.
2. Implement a `<Provider>ModelCustomizer : RelationalModelCustomizer`.
3. Add an `Add<Provider>Persistence()` extension.
4. Generate initial migrations.
5. Wire into host `Program.cs` files.

## Postgres migration race across silos

Every silo (Api/Web/Mcp/Worker) calls `EnsureDatabaseSchemaAsync` at startup, which under Postgres calls `MigrateAsync`.

EF Core's per-migration lock is acquired only during `__EFMigrationsHistory` writes, not while running the migration SQL — so two silos that both observe a migration as pending can both try to `CREATE TABLE` and the loser fails with `relation "X" already exists`.

`EnsureDatabaseSchemaAsync` wraps `MigrateAsync` in a session-level `pg_advisory_lock(8723547283)` (via a pinned connection from `OpenConnectionAsync`) to serialize concurrent migration attempts cleanly. The lock key is arbitrary but must be the same across all silos. SQLite uses `EnsureCreatedIgnoreRaces` and is unaffected.

## Custom-task catalog persistence

`CustomTaskCatalogGrain` uses `IPersistentState<CustomTaskCatalogState>` keyed by `GrainStorageNames.CustomTaskCatalog`, backed by `EfCoreCustomTaskCatalogGrainStorage` and the `CustomTaskCatalogEntries` table (composite PK on `(TaskType, SiloName)`; `ParameterSchemaJson` stored as a JSON text column via `System.Text.Json`).

Test clusters must register memory grain storage for this name in `WorkflowTestBase` alongside the other registries.

## Test parity (Sqlite ↔ PostgreSQL)

Every EF/grain-storage class in `Fleans.Persistence.Tests` is parametrised via `[DataTestMethod] [DataRow(PersistenceProvider.Sqlite)] [DataRow(PersistenceProvider.Postgres)]` against the `Infrastructure/PersistenceTestBase` fixture.

Default `dotnet test` runs only the SQLite rows (no Docker). To exercise the PG rows locally:

```bash
cd src/Fleans
FLEANS_PG_TESTS=1 dotnet test --filter "TestCategory=Postgres"
```

Requires Docker (Testcontainers boots `postgres:16-alpine`). Without `FLEANS_PG_TESTS=1` the PG rows surface as `Inconclusive` (non-failing) — never `Failed`.

CI runs the dedicated `PostgreSQL tests` job (`.github/workflows/pg-tests.yml`) on every PR.

**Bump `PostgresImage` in `Infrastructure/PostgresContainerFixture.cs` whenever the production deploy target moves** — today it tracks Aspire's `Aspire.Hosting.PostgreSQL` default (PG 16).

## `Persistence:MaxEventsPerLoad` safety valve (default `1000`)

`EfCoreEventStore.ReadEventsAsync` throws `InvalidOperationException` if the unread event delta exceeds this cap, surfacing broken-snapshotting scenarios before they OOM the silo. Increase via the `Persistence__MaxEventsPerLoad` env var for deployments with legitimately large deltas.

`FleansPersistenceOptions` lives in `Fleans.Persistence` (not `Fleans.ServiceDefaults`) to avoid a circular project reference.

## User-task list filter pushdown (audit #415)

`GetPendingUserTasks(assignee, candidateGroup, page)` pushes the assignee/candidateGroup filter to SQL on PostgreSQL via JSON-encoded `LIKE` patterns in raw SQL (`FromSqlInterpolated`); SQLite retains the existing in-memory filter.

`EF.Functions.Like` against a value-converted column (the JSON-serialized `IReadOnlyList<string>`) is not usable — EF applies the value converter to the LIKE pattern constant. **Use `FromSqlInterpolated` for any future column-level JSON filter on the same pattern.**

Strategy: `IUserTaskFilterStrategy` / `PostgresUserTaskFilterStrategy` in `Fleans.Persistence` / `Fleans.Persistence.PostgreSql`.
