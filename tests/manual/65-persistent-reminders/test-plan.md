# Manual Test Plan — #65 Persistent reminders survive silo restart

Verifies #650: BPMN timers persist across silo restarts and across Core-only
restarts in a multi-silo cluster.

## Prerequisites

- Compose-bundle topology built (`aspire publish --project Fleans.Aspire -t docker-compose -o out/compose`)
- `docker compose` available
- Redis container provisioned and healthy

## Steps

### Step A — Single-silo restart

1. `cd out/compose && docker compose up -d`
2. Deploy `timer-restart.bpmn` via the API (`POST /Workflow/process-definitions`).
3. Start an instance via `POST /Workflow/instances` for that definition.
4. Confirm reminder registered: open `https://localhost:<web-port>/dashboard/Reminders`.
   The entry for `TimerCallbackGrain` keyed by `<workflowInstanceId>:Timer_xxx` is visible.
5. Restart the Core silo:
   ```bash
   docker compose restart fleans-core
   ```
6. Wait for the 5-minute timer to fire (the BPMN has `PT5M`).
7. **Pass:** the workflow completes; verify via `GET /Workflow/instances/<id>/state` returns `IsCompleted: true`.

### Step B — Multi-silo cluster (Core-only restart)

Step B (multi-silo Core-only restart) runs against the Compose-bundle topology
only. Aspire-dev runs Core+Worker in a single process, so 'restart Core' is the
same as 'restart everything', which Step A already covers.

1. Confirm both `fleans-core` and `fleans-worker` are `Up` (`docker compose ps`).
2. Deploy `timer-restart.bpmn` and start an instance (same flow as Step A 2-3).
3. Confirm reminder registered in `/dashboard/Reminders`.
4. Restart **only** `fleans-core`:
   ```bash
   docker compose restart fleans-core
   ```
   Leave `fleans-worker` running.
5. Wait for the timer to fire.
6. **Pass:** workflow completes; the reminder fired on the (new) Core silo.

## Pass criteria

- Both Step A and Step B complete the workflow after the timer fires post-restart.
- No reminder duplication observed (single fire per timer).
- `fleans-core` logs show the reminder service initialised against Redis on
  silo startup (search for `Microsoft.Orleans.Reminders.Redis` / `Redis reminder`).

### Step C — Postgres reminders single-silo restart (#669)

1. `cd out/compose && docker compose up -d` with `FLEANS_PERSISTENCE_PROVIDER=Postgres`
   AND `Fleans__Reminders__Provider=Postgres` on the `fleans-core` service.
2. After startup, confirm the reminder table was auto-created:
   ```bash
   docker compose exec postgres psql -U fleans -d fleans \
     -c 'SELECT count(*) FROM "OrleansRemindersTable";'
   # → 0 (table exists, no rows yet)
   docker compose exec postgres psql -U fleans -d fleans \
     -c "SELECT count(*) FROM \"OrleansQuery\" WHERE \"QueryKey\" = 'ReadRangeRows1Key';"
   # → 1 (Main.sql ran and inserted the query template)
   ```
3. Deploy `timer-restart.bpmn` and start an instance (same as Step A 2-3).
4. Confirm a reminder row was persisted:
   ```bash
   docker compose exec postgres psql -U fleans -d fleans \
     -c 'SELECT "GrainId","ReminderName","StartTime" FROM "OrleansRemindersTable";'
   ```
5. Restart `fleans-core`. Wait for the timer to fire.
6. **Pass:** the workflow completes and the reminder row disappears
   (TimerCallbackGrain unregistered itself in `ReceiveReminder`).

### Step D — Mixed-storage fail-fast (SQLite app + Postgres reminders)

With the default `FLEANS_PERSISTENCE_PROVIDER=Sqlite` set, set
`Fleans__Reminders__Provider=Postgres` on the `fleans-core` service and restart.

**Pass:** silo refuses to start. `fleans-core` logs show
`InvalidOperationException: Fleans:Reminders:Provider=Postgres requires
Persistence:Provider=Postgres (got 'Sqlite'). Mixed-storage … unsupported`.

## Failure-mode regression (fail-fast, Redis path)

A separate check: with `orleans-redis` unset (e.g. `docker compose stop redis`),
restart `fleans-core` and confirm the silo **fails to start** with an
`InvalidOperationException` from `AddFleansReminders`. This proves the
fail-fast guard is wired correctly.

## Cleanup

```bash
docker compose down
```
