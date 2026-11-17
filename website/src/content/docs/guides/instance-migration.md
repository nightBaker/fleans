---
title: Instance migration
description: Move running process instances to a newer (or older) version of their process.
---

A deploy creates a new version of a process. Instances that are already running stay on the
version they started on. **Instance migration** moves running instances to another version of
the same process, for example to pick up a bug fix in v2 for instances started on v1.

A migrated instance keeps its **id, variables, history and waits**. A user task stays assigned
and claimed, a timer keeps its due time, and a message subscription keeps its correlation key.
Only the definition changes, so the instance continues on the target version's graph.

## How elements are matched

Each active element of the instance (a waiting user task, timer, message catch, a running
sub-process, a join with waiting tokens) moves to the target element with **the same id**.
To match an element with a different id (it was renamed), give an explicit
**mapping**: `review → approve`.

The target element must:

- exist, and have the same type (a user task stays a user task);
- sit in the same scope (the process, or the mapped sub-process);
- keep its multi-instance kind (parallel / sequential) and inner activity type;
- call the same process (call activity) and keep the same task type (custom task).

Finished elements keep their old ids in the history. The instance page shows them as they ran.

## Validation and dry run

Every instance validates itself before it moves. Use a **dry run** to see what would move without
changing anything. The result is per instance: `Migratable` (dry run), `Migrated`,
`AlreadyMigrated`, `Rejected` (with codes), or `Failed` (for example an unknown instance id).

| Rejection code | Why the instance cannot move |
|---|---|
| `not-running` | The instance completed, failed or was cancelled. |
| `same-version` | The instance already runs the target version. |
| `other-process` | The target is a version of a different process. |
| `invalid-mapping` | A mapping names an element that is not in the source or target version, or maps one element twice. |
| `unmapped-activity` | An active element (or a finished compensable activity) has no element in the target. Map it explicitly. |
| `type-changed` | The mapped element has another type. |
| `scope-changed` | The mapped element moved into or out of a sub-process. |
| `multi-instance-changed` | Multi-instance was added or removed, changed from parallel to sequential, or its inner activity changed type. |
| `join-changed` | A join with waiting tokens gets other incoming flows, another type or another activation condition. |
| `in-flight-work` | A script is running, a gateway is evaluating conditions, or callbacks are queued. Retry a moment later. |
| `compensation-running` | A compensation is running. Retry once it has finished. |
| `call-activity-changed` | A waiting call activity calls another process in the target. |
| `external-task-type-changed` | A waiting custom task has another task type in the target. |
| `event-subprocess-changed` | The timer, message or signal start events of an event sub-process in an open scope change. |

A rejected instance stays exactly as it was and keeps running on its version.

## Timers, messages and signals

By default a migration **does not move a deadline or a subscription silently**:

- A wait that is in both versions is **kept as it is**, even if the target defines it differently.
  A timer keeps its due time; a message catch keeps its subscription.
- A wait that the target **removes** (for example a boundary timer that v2 dropped) is cancelled.
- A wait that the target **adds** (a new boundary timer, message or signal) is registered,
  counting from the migration.
- A renamed wait is re-registered under its new id. A timer keeps the time it had left.

Turn on **Update event triggers** to re-arm the waits whose definition changed from the target
version: the timer gets the target's duration (counting from when its element started), and a
message catch subscribes to the target's message name and correlation key.

A non-interrupting boundary event that already fired is not armed again.

## User tasks

A waiting user task keeps its claim when the claimant still qualifies under the target version's
assignment. That is the case when the target names them as assignee or candidate user, keeps
every candidate group of the source, or has no restriction. Otherwise the task is unclaimed and
goes back to its candidates. The task list (`GET /UserTasks`) follows a renamed task.

## Call activities

A waiting call activity moves with the instance; its child instance stays on its own version and
reports back to the migrated element. To move children too, migrate the child process's
instances separately.

## From the Admin UI

- **Workflows** page: **Migrate** on a version opens the dialog for every running instance of that
  version. It shows how many instances are running, the element mapping (each element's
  same-id match, with problems highlighted), and an **Update event triggers** option. Click
  **Dry run** to see the per-instance outcome, then **Migrate**. Large versions migrate a batch at a
  time; the dialog offers **Migrate next batch** while instances are left.
- **Process instance** page: **Migrate** opens the same dialog for that one instance. After
  closing it the page reloads on the new version's diagram.
- The instance's journal and the debugger show an **Instance migrated** step: the versions and
  the renamed elements.

## From the REST API

Preview the default mapping between two versions:

```bash
curl "http://localhost:8081/Definitions/order/versions/1/migration-preview?to=2"
```

```json
{
  "processDefinitionKey": "order",
  "sourceProcessDefinitionId": "order:1:20261101T…",
  "sourceVersion": 1,
  "targetProcessDefinitionId": "order:2:20261102T…",
  "targetVersion": 2,
  "runningInstanceCount": 42,
  "elements": [
    { "sourceActivityId": "review", "sourceActivityType": "UserTask",
      "targetActivityId": null, "targetActivityType": null, "problem": "unmapped-activity" }
  ],
  "targetElements": [ { "activityId": "approve", "activityType": "UserTask" } ]
}
```

Dry run, then migrate every running v1 instance:

```bash
curl -X POST http://localhost:8081/Instances/migrate \
  -H 'Content-Type: application/json' \
  -d '{
    "sourceProcessDefinitionId": "order:1:20261101T…",
    "targetProcessDefinitionId": "order:2:20261102T…",
    "mappings": [ { "sourceActivityId": "review", "targetActivityId": "approve" } ],
    "dryRun": true,
    "requestId": "order-v1-to-v2"
  }'
```

The response counts outcomes and rejection codes and lists each instance:

```json
{
  "requestId": "order-v1-to-v2",
  "dryRun": true,
  "outcomes": { "Migratable": 40, "Rejected": 2 },
  "rejectionCodes": { "in-flight-work": 2 },
  "results": [ … ],
  "continuationToken": null
}
```

Send it again with `"dryRun": false` to migrate. One request handles at most
`Fleans:Migration:MaxBatchSize` instances (default 100). When more are left, the response
carries a `continuationToken`; pass it back for the next batch. Or name instances directly with
`"instanceIds": [...]` instead of `sourceProcessDefinitionId`.

One instance: `POST /Instances/{id}/migrate` with the same fields (no selector). It answers `409`
with the rejection codes when the instance cannot move.

### Retrying is safe

Each instance's migration id is `{requestId}:{instanceId}`. Sending the same request again is a
no-op for the instances it already moved (`AlreadyMigrated`), so retry a request that timed out
with the same `requestId`. Without a `requestId` one is generated per request.

Details: [REST API reference](/fleans/reference/api/#instance-migration-endpoints).

## Limits

- Event sub-processes whose start events change are rejected (`event-subprocess-changed`);
  their listeners are not re-armed in this version.
- Child instances of call activities are not migrated with their parent.
- `runningInstanceCount` comes from the read model, which runs slightly behind the instances.
  Each instance re-checks itself, so a lagging count can only turn into a `not-running` or
  `same-version` rejection, never a wrong move.
- There is no role check yet: anyone who may use the admin endpoints (purge, disable) may migrate.

## Configuration

| Key | Default | Purpose |
|---|---|---|
| `Fleans:Migration:MaxBatchSize` | `100` | Most instances one migrate request handles. |
| `Fleans:Migration:MaxParallelism` | `16` | Instances migrated at once within a request. |

See [Configuration](/fleans/reference/configuration/#instance-migration).

## Observability

- Metric `fleans.instance.migrations{outcome, process.definition.key}`: outcome `migrated`,
  `already_migrated`, `rejected` or `failed` (dry runs are not counted).
- Span `fleans.instance.migrate_batch` per request.
- Logs: EventIds 1170–1174 (instance), 14600–14602 (batch service).

See [Observability](/fleans/reference/observability/).
