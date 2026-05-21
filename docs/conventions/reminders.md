# Reminders

Orleans reminders persist BPMN timers across silo restarts. Fleans defaults to
the **Redis** reminder provider, sharing the `orleans-redis` connection used
for clustering and Pub-Sub streaming. Operators on `Persistence:Provider=Postgres`
can opt their reminders into Postgres too via `Fleans:Reminders:Provider=Postgres`
(see provider matrix below).

## Provider matrix

| `Persistence:Provider` | `Fleans:Reminders:Provider` | Outcome |
|---|---|---|
| `Sqlite` (default) | `Redis` (default) | Supported. Single-binary story preserved. |
| `Sqlite` | `Postgres` | **Unsupported — silo refuses to start** (#669). |
| `Postgres` | `Redis` (default) | Supported. Reminders ride the clustering Redis. |
| `Postgres` | `Postgres` | Supported per #669. Reminders share `ConnectionStrings:fleans`. |

`Fleans:Reminders:Provider` is bound at startup; values are matched
case-insensitively. Unknown values throw `ArgumentException`.

## Postgres reminders schema

When `Fleans:Reminders:Provider=Postgres`, `EnsureDatabaseSchemaAsync` runs two
vendored upstream scripts under the same advisory lock used by app migrations:

- `PostgreSQL-Main.sql` creates the `OrleansQuery` query-template table
  (required by the reminders script's `INSERT INTO OrleansQuery` statements).
- `PostgreSQL-Reminders.sql` creates `OrleansRemindersTable` plus the upsert /
  read / delete query templates.

The scripts are bundled as `<EmbeddedResource>` in `Fleans.ServiceDefaults`
(under `Resources/`) and are pinned to the matching `Microsoft.Orleans.Reminders.AdoNet`
package version (10.0.1 today). Apache-2.0 attribution lives in each file's
header and in `LICENSE-orleans-vendor` at the repo root.

The schema-init is idempotent — it checks for `OrleansQuery` existence via
`SELECT EXISTS (... FROM pg_class WHERE relname = 'orleansquery')` and skips
both scripts if already present. Concurrent silos serialise via the existing
session-level advisory lock (`pg_advisory_lock`).

If schema-init throws (script error, connection failure), the exception
propagates and the silo refuses to start — same registration-vs-cleanup
asymmetry that the Redis fail-fast guard enforces.

## Why Redis (not AdoNet) by default

- Redis is already required for clustering + streaming — no new infrastructure.
- `Microsoft.Orleans.Reminders.AdoNet` does not support SQLite, which would
  force every SQLite quick-start user to also install Postgres for timer
  durability — breaking the SQLite single-binary story.
- Reminders are decoupled from the application persistence pivot. SQLite
  users get full timer durability via Redis.

## Fail-fast semantics

Silos throw at startup if the `orleans-redis` connection string is missing.
BPMN timer durability is a correctness invariant (per CLAUDE.md's
"Registration-vs-cleanup error asymmetry"); silent fallback to in-memory
would re-create the exact bug this configuration prevents.

The wiring lives in `Fleans.ServiceDefaults/Reminders/`:

- `FleansReminderOptions.cs` — pins the Redis connection name (`orleans-redis`).
- `FleansRemindersExtensions.cs` — `AddFleansReminders(IConfiguration)` extension
  used from `Fleans.Api/Program.cs` and `Fleans.WorkerHost/Program.cs`.
  `ResolveRemindersConfiguration(IConfiguration)` is the testable validation
  surface (see `Fleans.ServiceDefaults.Tests/FleansRemindersExtensionsTests.cs`).

## Multi-silo cluster invariants

- The reminder service is **cluster-wide** when backed by Redis: all silos
  see the same reminder set, and the Orleans reminder service handles
  redelivery on silo placement changes.
- `TimerCallbackGrain` is `[CorePlacement]` — reminders are persisted by
  any silo but the callback fires on a Core silo. Validated in manual
  regression #65 (Core-only restart while Worker stays up).

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
