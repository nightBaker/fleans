# Reminders

Orleans reminders persist BPMN timers across silo restarts — every pending timer is one
`TimerCallbackGrain` with one reminder. Fleans defaults to the **Redis** reminder provider,
sharing the `orleans-redis` connection used for clustering and Pub-Sub streaming. **Production
deployments with many pending timers should use `Fleans:Reminders:Provider=Postgres` in their own
database** (`ConnectionStrings:fleans-reminders`, see [Scale](#scale-why-postgres-in-its-own-database)).

## Provider matrix

| `Persistence:Provider` | `Fleans:Reminders:Provider` | Outcome |
|---|---|---|
| `Sqlite` (default) | `Redis` (default) | Supported. Single-binary story preserved. |
| `Sqlite` | `Postgres` | **Unsupported — silo refuses to start** (#669). |
| `Postgres` | `Redis` (default) | Supported. Reminders ride the clustering Redis. |
| `Postgres` | `Postgres` | Supported (#669). **Recommended for production** (#886). Reminders use `ConnectionStrings:fleans-reminders` when set, otherwise share `ConnectionStrings:fleans`. |

`Fleans:Reminders:Provider` is bound at startup; values are matched
case-insensitively. Unknown values throw `ArgumentException`.

## Tuning: `Fleans:Reminders` → Orleans `ReminderOptions`

`AddFleansReminders` binds the `Fleans:Reminders` section onto Orleans' `ReminderOptions`
(the `Provider` key is ignored there). Unset keys keep the Orleans defaults:

| Key | Orleans default | What it does |
|---|---|---|
| `Fleans:Reminders:ReminderLoadingWindow` | `00:10:00` | Only reminders due within this window are held in memory with a local timer (Orleans ≥ 10.3). Must be ≥ `RefreshReminderListPeriod`. If the reminder store is unreachable for longer than the window, timers fire late. |
| `Fleans:Reminders:RefreshReminderListPeriod` | `00:05:00` | How often each silo re-reads its whole slice of the reminder ring from the store. |
| `Fleans:Reminders:InitializationTimeout` | `00:05:00` | How long a silo retries the first reminder-table read before giving up. |

Keep `MinimumReminderPeriod` at its 1-minute default: `TimerCallbackGrain` and
`TimerStartEventSchedulerGrain` rely on it (see the period clarifier below).

## Scale: why Postgres in its own database

How the reminder service works (Orleans 10.4, checked against the source):

- The grain ring is split between silos. Each silo reads **every** reminder of its range from
  the store every `RefreshReminderListPeriod` — at 10 M pending timers that is about 33 k rows/s
  cluster-wide even when none fires.
- Since Orleans 10.3 only reminders due within `ReminderLoadingWindow` are kept in memory with a
  timer; the rest stay in the store. That bounds silo memory, not the periodic re-read.
- The Redis provider keeps every reminder of the cluster in one sorted set (`{ServiceId}/reminders`).
  One key lives on one Redis node, so Redis Cluster cannot spread it.
- The Postgres provider reads ranges with `ServiceId = @ServiceId AND GrainHash > @BeginHash AND
  GrainHash <= @EndHash`. Fleans adds the `IX_RemindersTable_ServiceId_GrainHash` index for it
  (below), so a range read touches only its slice.

A dedicated database (`ConnectionStrings:fleans-reminders`) keeps the periodic range reads off
the journal / read-model database. It can live on the same Postgres server or its own.
`tests/load/scripts/timers-1m.js` (`timers-1m`, see `tests/load/README.md`) measures timer lag at
1 M timers due in one minute; if it fails (p99 > 2 s), the roadmap's step 2 — sharded timer
scheduler grains — replaces per-timer reminders.

## Postgres reminders schema

When `Fleans:Reminders:Provider=Postgres`, `EnsureDatabaseSchemaAsync` runs two
vendored upstream scripts:

- `PostgreSQL-Main.sql` creates the `OrleansQuery` query-template table
  (required by the reminders script's `INSERT INTO OrleansQuery` statements).
- `PostgreSQL-Reminders.sql` creates `OrleansRemindersTable` plus the upsert /
  read / delete query templates, and — Fleans' only change to the upstream script —
  `CREATE INDEX IX_RemindersTable_ServiceId_GrainHash ON OrleansRemindersTable (ServiceId, GrainHash)`
  for the range reads (#886). Pre-1.0 rule: the script is changed in place; a fresh reminders
  database is the upgrade path.

Without `fleans-reminders` the scripts run against the `fleans` database, inside the advisory lock
held for app migrations. With it, they run against the reminders database on their own connection,
under the same advisory-lock key. The database itself must exist — Fleans creates tables, not
databases.

The scripts are bundled as `<EmbeddedResource>` in `Fleans.ServiceDefaults`
(under `Resources/`) and are pinned to the matching `Microsoft.Orleans.Reminders.AdoNet`
package version (10.4.0 today; the upstream scripts are unchanged since 10.0.1). Apache-2.0
attribution and the modification notice live in each file's header and in
`LICENSE-orleans-vendor` at the repo root. When re-vendoring, keep the index.

The schema-init is idempotent — it checks for `OrleansQuery` existence via
`SELECT EXISTS (... FROM pg_class WHERE relname = 'orleansquery')` and skips
both scripts if already present. Concurrent silos serialise via the
migration lock (a transaction-scoped `pg_advisory_xact_lock` on its own connection — see
`persistence.md`; it also works through PgBouncer in transaction mode, #923).

If schema-init throws (script error, connection failure), the exception
propagates and the silo refuses to start — same registration-vs-cleanup
asymmetry that the Redis fail-fast guard enforces.

Tests: `Fleans.Persistence.Tests/PostgresRemindersSchemaTests.cs` (`FLEANS_PG_TESTS=1`) creates the
schema in a dedicated database and asserts with `EXPLAIN` that a range read uses the index.

## Why Redis (not AdoNet) by default

- Redis is already required for clustering + streaming — no new infrastructure.
- `Microsoft.Orleans.Reminders.AdoNet` does not support SQLite, which would
  force every SQLite quick-start user to also install Postgres for timer
  durability — breaking the SQLite single-binary story.
- Reminders are decoupled from the application persistence pivot. SQLite
  users get full timer durability via Redis.

## Fail-fast semantics

Silos throw at startup if the chosen provider's connection string is missing
(`orleans-redis` for Redis; `fleans-reminders` or `fleans` for Postgres).
BPMN timer durability is a correctness invariant (per CLAUDE.md's
"Registration-vs-cleanup error asymmetry"); silent fallback to in-memory
would re-create the exact bug this configuration prevents.

The wiring lives in `Fleans.ServiceDefaults/Reminders/`:

- `FleansReminderOptions.cs` — connection names (`orleans-redis`, `fleans-reminders`) and the
  `Fleans:Reminders` section name.
- `FleansRemindersExtensions.cs` — `AddFleansReminders(IConfiguration)` extension
  used from `Fleans.Api/Program.cs` and `Fleans.WorkerHost/Program.cs`.
  `ResolveRemindersConfiguration(IConfiguration)` is the testable validation
  surface (see `Fleans.ServiceDefaults.Tests/FleansRemindersExtensionsTests.cs`).

## Multi-silo cluster invariants

- The reminder service is **cluster-wide** with either durable provider: all silos
  see the same reminder set, and the Orleans reminder service handles
  redelivery on silo placement changes.
- **Every silo runs the same provider** — including plugin hosts, which own a slice of the
  reminder ring (#788). `AddFleansPluginHost` wires it from `Fleans:Reminders:Provider`
  (`AddFleansPluginReminders` in `Fleans.Worker`, which mirrors `AddFleansReminders` without
  referencing `Fleans.ServiceDefaults`) and fails fast without the connection string.
- `TimerCallbackGrain` is `[CorePlacement]` — reminders are persisted by
  any silo but the callback fires on a Core silo. Validated in manual
  regression #65 (Core-only restart while Worker stays up).
- **A registration that lands on a non-owner silo fires late.** Since Orleans 10.4 a silo that
  receives `RegisterOrUpdateReminder` for a grain outside its ring range only stores the row (warning
  "I shouldn't have received request 'RegisterReminder'…", EventId 102910). The owner loads it at
  its next `RefreshReminderListPeriod`, up to 5 minutes later. In a steady cluster this should not
  happen. If the warning keeps appearing, the callers' ring is wrong. The known cause, a silo of a
  fresh cluster excluding itself from the ring, is worked around in `AddFleansClustering`
  ([clustering.md § Ring guard](clustering.md#ring-guard-a-joining-silo-reported-dead-to-itself-902)).
  Lowering `RefreshReminderListPeriod` is not a fix: every silo then re-reads its whole slice of
  the reminder table that often.

## Migration note

Existing deployments running `UseInMemoryReminderService` will lose any
**already-scheduled** in-memory reminders at the upgrade boundary. This is
functionally identical to the current restart behavior (in-memory reminders
are dropped on every restart today) — no additional remediation required.

## TimerCallbackGrain period clarifier

`TimerCallbackGrain.Activate` calls `RegisterOrUpdateReminder(name, dueTime,
TimeSpan.FromMinutes(1))`. The 1-minute second argument is Orleans's
**required minimum reminder period** (not a bug). The grain unregisters
itself in `ReceiveReminder` (see `TimerCallbackGrain.cs:44, :66`) after the
first fire, so the period never recurs in practice for non-cycle timers.
For cycle timers, the grain re-registers explicitly with the next cycle
due time (`TimerCallbackGrain.cs:79`).

## Aspire dev experience

`Fleans.Aspire/Program.cs` no longer wires `.WithMemoryReminders()` on the
Orleans AppHost resource. Aspire's role here is orchestration — provision
the Redis container, expose the `orleans-redis` connection — and the silo
configures the reminder provider via the `AddFleansReminders` helper.
