# Runbook: purging finished workflow instances (retention)

Fleans never deletes a finished instance on its own. Every instance keeps its journal
(`WorkflowEventBatches`), its snapshot (`WorkflowSnapshots`) and its read-model rows
(`WorkflowInstances` plus entries, variable scopes, condition results, user tasks, …) until you
purge it. A 10-step instance stores roughly 15 KB, so a million finished instances is about
15 GB. Issue: [#862](https://github.com/nightBaker/fleans/issues/862).

The purge is one admin endpoint, `POST /Instances/purge`. Nothing runs it on a schedule: call it
from your own cron job, CI job or by hand.

## What it deletes

An instance is purged when all of these hold:

- it **finished** — completed, cancelled or failed (`IsCompleted` in the read model);
- it finished **before** the `CompletedBefore` cutoff;
- it is **not the child of a running instance**. A call-activity child whose parent is still
  running is kept until the parent finishes. A child whose parent was already purged is purged.

Running instances, and instances that never started, are never touched, however old they are.

For each purged instance it deletes:

| Data | Table(s) |
|---|---|
| Journal | `WorkflowEventBatches` |
| Snapshot | `WorkflowSnapshots` |
| Read model | `WorkflowInstances`, `WorkflowActivityInstanceEntries`, `WorkflowVariableStates`, `WorkflowConditionSequenceStates`, `GatewayForks`, `ComplexGatewayJoinStates`, `TimerCycleTracking` |
| User tasks | `UserTasks` |

It also deletes **message idempotency records** (`MessageDeduplications`, one per message name
+ `IdempotencyKey`, #977) received more than 24 hours ago — their window has passed, so the engine
already ignores them. This does not depend on `CompletedBefore`. At most `MaxInstances` such
records go per call, in batches of `BatchSize`; if the limit is hit, `hasMore` is `true`.

It does **not** delete process definitions, message/signal correlation state, or timer
reminders. A finished instance has already removed its subscriptions and reminders.

## How it works

`EfCoreWorkflowInstancePurgeService` (`Fleans.Persistence`) runs a loop of batches:

1. It selects up to `BatchSize` candidate ids from the **read model**, oldest `CompletedAt`
   first. `IX_WorkflowInstances_CompletedAt` serves this query.
2. For each id it calls `IEventStore.DeleteInstanceAsync(id)`, which deletes that instance's
   journal and snapshot **by `InstanceId`**. This is the only way the journal is ever accessed.
3. It deletes the batch's read-model rows in one transaction, child tables first and
   `WorkflowInstances` last.

The journal goes first. If the purge stops between steps 2 and 3 (a crash, a cancelled request),
the read-model row is still there, so the next call selects the instance again. Deleting a journal
that is already gone does nothing, so a retry is always safe.

The loop stops after `MaxInstances` instances. This keeps each HTTP request short. The response
says whether more instances still match.

## Running it

```bash
# Purge everything that finished more than 90 days ago, 10 000 instances per call.
CUTOFF=$(date -u -d '90 days ago' +%Y-%m-%dT%H:%M:%SZ)   # macOS: date -u -v-90d +%Y-%m-%dT%H:%M:%SZ
while :; do
  RESP=$(curl -sf -X POST "$FLEANS_API/Instances/purge" \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $TOKEN" \
    -d "{\"CompletedBefore\":\"$CUTOFF\"}") || exit 1
  echo "$RESP"
  echo "$RESP" | grep -q '"hasMore":true' || break
done
```

Request body:

| Field | Required | Default | Notes |
|---|---|---|---|
| `CompletedBefore` | yes | — | ISO-8601 timestamp. Must be at least **1 hour** in the past, otherwise `400`. |
| `BatchSize` | no | `500` | Instances per batch, `1`–`5000`. |
| `MaxInstances` | no | `10000` | Instances per call, `≥ 1`. |

Response: `{"purgedCount": 10000, "hasMore": true, "purgedDeduplicationCount": 120}`. Call again until `hasMore` is `false`.

The endpoint uses the `admin` rate-limit policy. When authentication is on
(`Authentication:Authority`), it requires an authenticated caller, like every other endpoint.
There is no separate admin role, so restrict who can reach it at your gateway if needed.

**Logs:** 14100 purge started, 14101 batch purged, 14102 purge finished, 14103 expired idempotency records purged (all Information).

**First purge on a large database:** run it off-peak and with a smaller `BatchSize` (for example
`200`) so that each read-model transaction stays short. On PostgreSQL, run `VACUUM` (or let
autovacuum catch up) afterwards. On SQLite the file does not shrink until you run `VACUUM`, which
locks the database while it runs.

## Purged ids and live grains

Purging does not touch the Orleans cluster. Keep these cases in mind:

- **Reading a purged id.** `GET /Instances/{id}/state` reads the read model first. With no row it
  returns `404` without activating the grain.
- **A later activation of a purged id** finds an empty journal and starts as an empty instance,
  exactly like an id that never existed. It writes nothing to the read model.
- **A grain still active in memory.** A finished instance stays activated until Orleans collects
  it as idle (15 minutes by default). Until then it answers calls from its in-memory state. If
  something made it append another event after the purge, its journal would start mid-history,
  and its next activation would fail with a "journal has a gap" error. Finished instances do not
  append in normal operation. The **1-hour minimum age** on `CompletedBefore` keeps the purge away
  from instances that finished recently and are most likely still active.
- **Child instances.** A child is kept while its parent is running. Once both have finished,
  each is purged on its own cutoff. A parent or child purged before the other still names the
  other's id, and looking up the purged one returns "not found".

## Not covered (follow-ups)

- **Partitioning** of read-model tables by completion month, so that old months are dropped
  whole instead of deleted row by row (roadmap "Retention").
- **Archival** of an instance's journal to S3-compatible storage before deletion, for audit.
- **A scheduler** inside Fleans. Add one only when someone asks for it.
