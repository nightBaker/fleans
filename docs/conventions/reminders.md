# Reminders

Orleans reminders persist BPMN timers across silo restarts. Fleans uses
the **Redis** reminder provider, sharing the `orleans-redis` connection
used for clustering and Pub-Sub streaming.

## Why Redis (not AdoNet)

- Redis is already required for clustering + streaming — no new infrastructure.
- `Microsoft.Orleans.Reminders.AdoNet` does not support SQLite, which would
  force every SQLite quick-start user to also install Postgres for timer
  durability — breaking the SQLite single-binary story.
- Reminders are decoupled from the application persistence pivot
  (`FLEANS_PERSISTENCE_PROVIDER={Sqlite,Postgres}`). SQLite users get full
  timer durability via Redis.

## Fail-fast semantics

Silos throw at startup if the `orleans-redis` connection string is missing.
BPMN timer durability is a correctness invariant (per CLAUDE.md's
"Registration-vs-cleanup error asymmetry"); silent fallback to in-memory
would re-create the exact bug this configuration prevents.

The wiring lives in `Fleans.ServiceDefaults/Reminders/`:

- `FleansReminderOptions.cs` — pins the connection name (`orleans-redis`).
- `FleansRemindersExtensions.cs` — `AddFleansReminders(IConfiguration)` extension
  used from `Fleans.Api/Program.cs` and `Fleans.WorkerHost/Program.cs`.

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
