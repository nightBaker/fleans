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

It does **not** delete process definitions, message/signal correlation state, or timer
reminders. A finished instance has already removed its subscriptions and reminders.

## How it works

`EfCoreWorkflowInstancePurgeService` (`Fleans.Persistence`) runs a loop of batches:

1. It selects up to `BatchSize` candidate ids from the **read model**, oldest `CompletedAt`
   first. `IX_WorkflowInstances_CompletedAt` serves this query.
2. For each id it calls `IEventStore.DeleteInstanceAsync(id)`, which deletes that instance's
   journal and snapshot **by `InstanceId`**. This is the only way the journal is ever accessed.
3. It deletes the batch's read-model rows in one transaction, child tables first and
   `WorkflowInstances` last. The details (entries, variable scopes, condition results) go
   through `IReadModelRetention`: deleted row by row on SQLite, and on PostgreSQL only from the
   default partitions (see below).
4. After the batches, `IReadModelRetention.ReclaimAsync` drops the PostgreSQL partitions of the
   months that are fully purged.

The journal goes first. If the purge stops between steps 2 and 3 (a crash, a cancelled request),
the read-model row is still there, so the next call selects the instance again. Deleting a journal
that is already gone does nothing, so a retry is always safe.

The loop stops after `MaxInstances` instances. This keeps each HTTP request short. The response
says whether more instances still match.

After the instance purge, the same call removes expired `MessageDeduplications` records (#977),
including when no instances matched. Their expiry is `ReceivedAt + 24 hours`, independent of
`CompletedBefore`; equality is expired. The index on `ReceivedAt` serves this scan. At most
`MaxInstances` message keys are examined per call, `BatchSize` at a time, in addition to the
instance budget. Expiry is checked again in the DELETE so a concurrent send that refreshes a
selected key keeps its new record. A loaded grain can reuse a purged, expired key: the storage
writer inserts the new record when the old row is absent.

## PostgreSQL: completion-month partitions (#980)

On PostgreSQL, `WorkflowActivityInstanceEntries`, `WorkflowVariableStates` and
`WorkflowConditionSequenceStates` are range-partitioned by `InstanceCompletedAt`, the
`CompletedAt` of the instance the row belongs to (migration `PartitionReadModelDetails`).
`PartitionedReadModelRetention` (`Fleans.Persistence.PostgreSql`) owns the partitions:

- **One partition per UTC month,** `<Table>_y2026m11`, plus `<Table>_default` for rows of a
  month that has none. The projector writes an instance's details once, when it completes, so
  rows never move between partitions.
- **Created ahead:** the current month and the next two get their partitions at startup (after
  the migrations) and on every purge call. A month whose rows already went to the default
  partition (nothing created its partitions in time) gets none and stays in the default — EventId
  14107. Running the purge at least monthly avoids this.
- **A batch** deletes an instance's journal and its `WorkflowInstances` row as before, but its
  details only from the default partitions. The details in a month partition stay until the
  month is dropped. Every read goes through the instance row, so they are unreachable.
- **A month is dropped** — all three partitions, in one transaction — once it ends before
  `CompletedBefore` and no `WorkflowInstances` row that completed in it is left. The parents are
  locked first, so no projector write into the month is in flight while it is checked. A month
  still holding a kept instance (a child of a running parent) waits for it. A busy table (lock
  wait over 5 s, or a deadlock) skips the month until the next call — EventId 14108.
- **`WorkflowInstances` itself is not partitioned.** The projector's upsert of a running row
  conflicts on `"Id"`, and a partitioned table can only enforce uniqueness that includes the
  partition column. One row per instance is a small part of the read model.

Measured locally (PostgreSQL 16, 10-core laptop, Docker): 100 000 instances completed in one
month, each shaped like the `long-diverse` load test — 40 entries, 5 variable scopes (~500 B), 3
condition results; 1.1 GB of details in all.

| Reclaiming the month's details | Time | WAL | Disk afterwards |
|---|---|---|---|
| Row-by-row `DELETE`, 500 instances per transaction (before #980) | 8.1 s, then `VACUUM` 9.3 s | 1154 MB | 241 MB still held by bloat |
| Partition drop | 0.17 s | 161 kB | all returned |

The journal deletes by `InstanceId` cost the same on both paths. SQLite keeps the row-by-row
delete: it is for development only.

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
| `BatchSize` | no | `500` | Batch size for each category, `1`–`5000`. |
| `MaxInstances` | no | `10000` | Maximum instances and, separately, message keys examined per call, `≥ 1`. |

Response: `{"purgedCount": 10000, "hasMore": true, "purgedMessageDeduplicationsCount": 500}`.
`purgedCount` counts instances only; `purgedMessageDeduplicationsCount` counts deleted message
records. `hasMore` covers either category. Call again until it is `false`.

The endpoint uses the `admin` rate-limit policy. When authentication is on
(`Authentication:Authority`), it requires an authenticated caller, like every other endpoint.
There is no separate admin role, so restrict who can reach it at your gateway if needed.

**Logs:** 14100 instance purge started, 14101 instance batch purged, 14102 instance purge finished,
14103 expired message-deduplication records purged (all Information). On PostgreSQL also
14104 month of details dropped, 14106 month partitions created (Information), 14105 month kept
(Debug), 14107 month left in the default partition, 14108 partitions busy — skipped (Warning).

**First purge on a large database:** run it off-peak and with a smaller `BatchSize` (for example
`200`) so that each read-model transaction stays short. On PostgreSQL, run `VACUUM` (or let
autovacuum catch up) afterwards for `WorkflowInstances`, `UserTasks` and the default partitions;
dropped month partitions return their space at once. On SQLite the file does not shrink until
you run `VACUUM`, which locks the database while it runs.

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

- **Archival** of an instance's journal to S3-compatible storage before deletion, for audit
  ([#1020](https://github.com/nightBaker/fleans/issues/1020)).
- **A scheduler** inside Fleans. Add one only when someone asks for it.
