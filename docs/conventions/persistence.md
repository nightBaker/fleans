# Persistence providers

Two providers: **SQLite** (default, local dev) and **PostgreSQL** (production / load testing). Selected via configuration — no code changes needed.

## Configuration

- **Config key:** `Persistence:Provider` (values: `Sqlite` | `Postgres`, case-insensitive, default `Sqlite`). `AddFleansPersistence` throws `ArgumentException` on any other value (typos, empty, whitespace) so misconfigured deployments fail fast — mirrors the validation pattern in `FleanStreamingExtensions.cs:26`.
- **Aspire:** set `FLEANS_PERSISTENCE_PROVIDER=Postgres` before launch to auto-provision a Postgres container.
- **Connection strings:** SQLite uses `FLEANS_SQLITE_CONNECTION` / `FLEANS_QUERY_CONNECTION`. PostgreSQL uses `ConnectionStrings:fleans` (required) and `ConnectionStrings:fleans-query` (optional read replica).
- **Migration strategy:** SQLite uses `EnsureCreated()`. PostgreSQL uses `MigrateAsync()` (migrations applied automatically by `Fleans.Api` on startup).
- **Adding a column to an existing table:** generate a Postgres migration (`dotnet ef migrations add <Name> --project Fleans.Persistence.PostgreSql --context FleanCommandDbContext --output-dir Migrations/Command`). **Pre-1.0, skip the SQLite backfill** (`SqliteSchemaInitializer.AdditiveColumns`) — see the pre-1.0 rule in `CLAUDE.md`. `EnsureCreated()` is a no-op on an existing SQLite file, so after a schema change delete the stale dev DB (Aspire's `$TMPDIR/fleans-dev.db`, which E2E runs reuse) instead of hitting `no such column` at first query.
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

## Event store: journal, snapshots, read model

`WorkflowInstance` is event-sourced: the journal (`WorkflowEventBatches`) is the only source of truth. Snapshots and the read model derive from it. Design: [`docs/plans/2026-10-16-event-sourced-workflow-instance-design.md`](../plans/2026-10-16-event-sourced-workflow-instance-design.md).

- **Versions.** A grain's version is its event count. `IEventStore.AppendEventsAsync(instanceId, events, expectedVersion)` stores the batch as versions `expectedVersion + 1`, … — the store does the numbering, callers pass the count. `ReadEventsAsync(afterVersion)` returns `Version > afterVersion`.
- **One row per append (#867).** A `WorkflowEventBatches` row is one `ConfirmEvents` batch: `(InstanceId uuid, FirstVersion, LastVersion, Payload)`, keyed `(InstanceId, FirstVersion)`. That key is the concurrency check — two writers that both saw version *n* collide on `FirstVersion = n + 1`, whatever their batch lengths. Versions callers see (and snapshot versions) are always append boundaries, so `ReadEventsAsync` reads batches with `FirstVersion > afterVersion` and throws on a gap rather than replay a partial history. Per-event rows cost ~190 bytes of row and index overhead each, about half the journal; batching removes most of it.
- **Journal format: Orleans binary.** `Payload` is the batch as a `List<WorkflowEvent>` written by the Orleans serializer (the same one that carries events and state across grain calls and writes snapshots). Every event is `[GenerateSerializer]` with explicit `[property: Id(n)]` and a short `[Alias("ev.<Name>")]` — the alias is the type tag stored per event, so keep it short and never reuse one. Collections in events are `List<T>`. `JournalEventSerializationTests` round-trips every `WorkflowEvent` type; a new event without a codec fails there, not in production. Variable values keep their CLR types on replay (no JSON `int` → `long` drift). The payload is not readable with `SELECT`; decode it with the Orleans `Serializer` when debugging.
- **Variables are journaled as deltas.** `WorkflowExecution.Emit` reduces `ActivityCompleted` / `VariablesMerged` to the keys the merge changes (`WorkflowVariablesState.ChangesFrom`, compared by `VariableValues.AreEqual`). `Apply` merges key by key, so the state is identical live and on replay; a script task that returns its whole scope journals only what it wrote. Entry points cap incoming variables at `Fleans:Variables:MaxSizeBytes` (JSON bytes, default 1 MiB): `WorkflowInstance.EnsureVariablesWithinLimit` throws `ArgumentException` (REST 400; a script or custom task fails its activity; 1140, Warning) before anything is applied. `FailActivity` is the exception: the failure must land, so it drops oversized variables instead.
- **The append is the commit.** `ApplyUpdatesToStorage` returns `true` once the append succeeds; snapshot and projection writes after it log on failure and never throw. Throwing there makes Orleans' `CustomStorage` adaptor retry the same batch at the new version, which appends it twice. A failed projection write (1132, Warning) is retried by a keep-alive grain timer with doubling delays from 2 s, because the failed write may be the instance's last. On success it logs 1134. After 8 attempts (~8.5 min) it gives up (1135, Error) so a write that always fails cannot pin the grain, and the next activation catches up instead. A failed snapshot write logs 1131 at Error with the tail length: snapshots that keep failing let the tail grow past `MaxEventsPerLoad`, and then the instance cannot activate.
- **Snapshots are a cache.** `WorkflowSnapshots` holds one row per grain: the **full** `WorkflowInstanceState` (Orleans binary serialization) and the version it covers. A new snapshot is written once `Persistence:SnapshotInterval` (default `100`) events were appended since the last one. Activation loads it and replays the tail. A failed snapshot read is not worked around: Orleans' adaptor retries `ReadStateFromStorage` with backoff, so the activation waits (pre-1.0, a snapshot from an older build means "use a fresh database"). Anything that crosses into state must be Orleans-serializable: copy caller lists into `List<T>` (see `UserTaskMetadata.Initialize`).
- **The read model is write-only.** `IWorkflowStateProjection` writes the `WorkflowInstances` row after every append, with the version it reflects (shadow column `ProjectedVersion`). The grain never loads state from it.
- **Details only once the instance completes.** A running instance projects only its row — status, timestamps, business id: what lists and filters need. Its details (entries, variable scopes, condition results — what `GetStateSnapshot` serves) are written in the same `SaveChanges` that sets `IsCompleted`, so a finished instance is shown without activating its grain. `WorkflowQueryService.GetStateSnapshot` reads that flag: completed → the read model, running → `IWorkflowInstanceGrain.GetStateSnapshot()`. Both build the response with `InstanceStateSnapshots.From`. Rewriting every entry of a running instance on each append was most of Postgres's write load (#861). All writes go through `TryWriteProjection`, which serializes them (an append's write, the retry and the activation catch-up could otherwise interleave on one row) and never moves the projected version backwards. Members with no read-model columns (`UserTasks`, `CompensationLog`, `TransactionOutcomes`, `PendingOperations`, …) live only in the snapshot and journal. On activation, a projection behind the journal is rewritten in full (1133). Its version is read in parallel with the snapshot and the events, so the check adds no round trip.
- **A snapshot is exactly the state at its version.** An `[AlwaysInterleave]` durable enqueue can confirm while the main turn has applied events to `State` but not drained them; `ApplyUpdatesToStorage` then skips both writes (the main turn's drain does them). `EnqueueDurable` itself changes `State.PendingOperations` before its append and can interleave with another append's awaits. Log writes run one at a time, so it is still in flight (`EnqueueInFlight`: its `_enqueueLock` is held) when that append decides on a snapshot, and the snapshot is skipped. The read model does not project `PendingOperations`, so it is unaffected.
- **Replay is deterministic.** Every journaled event derives from `WorkflowEvent`, whose `OccurredAt` is stamped by `WorkflowExecution.Stamp` (called by `Emit` and by the grain's durable enqueue, which raises its event directly). The journal is typed on `WorkflowEvent` (`JournaledGrain<WorkflowInstanceState, WorkflowEvent>`, `IEventStore`), so nothing else can be journaled. `Apply` handlers take ids and times only from the event (state transition methods take the timestamp as a parameter), so replay rebuilds exactly the live state. Never read the clock or call `Guid.NewGuid()` in an `Apply` path.
- **Pre-1.0:** this layout needs a fresh database (Postgres: the `CompactJournal` migration; SQLite: delete the dev DB file). The migration does not carry old data forward — on a database that already holds snapshots it fails rather than wiping them.

## `Persistence:MaxEventsPerLoad` safety valve (default `1000`)

`EfCoreEventStore.ReadEventsAsync` throws `InvalidOperationException` if the unread event delta exceeds this cap, surfacing broken snapshotting before it OOMs the silo. It must be greater than `Persistence:SnapshotInterval` — `FleansPersistenceOptionsValidator` fails startup otherwise. Passing that check is not a guarantee: a very large batch appended just before a crash, or failing snapshot writes (1131), can still leave a longer tail. Increase via the `Persistence__MaxEventsPerLoad` env var for deployments with legitimately large deltas.

`FleansPersistenceOptions` lives in `Fleans.Persistence` (not `Fleans.ServiceDefaults`) to avoid a circular project reference.

## User-task list filter pushdown (audit #415)

`GetPendingUserTasks(assignee, candidateGroup, page)` pushes the assignee/candidateGroup filter to SQL on PostgreSQL via JSON-encoded `LIKE` patterns in raw SQL (`FromSqlInterpolated`); SQLite retains the existing in-memory filter.

`EF.Functions.Like` against a value-converted column (the JSON-serialized `IReadOnlyList<string>`) is not usable — EF applies the value converter to the LIKE pattern constant. **Use `FromSqlInterpolated` for any future column-level JSON filter on the same pattern.**

Strategy: `IUserTaskFilterStrategy` / `PostgresUserTaskFilterStrategy` in `Fleans.Persistence` / `Fleans.Persistence.PostgreSql`.
