# Instance migration: move running instances to another version

**Status:** implemented · **Date:** 2026-11-16 · **Issue:** [#1069](https://github.com/nightBaker/fleans/issues/1069)

User guide: [`website/src/content/docs/guides/instance-migration.md`](../../website/src/content/docs/guides/instance-migration.md).

## Problem

A deploy creates version N+1; running instances stay bound to the `ProcessDefinitionId` they
started on until they finish. A bug fix in v2 does not reach the instances already waiting in v1,
and the only workaround was to cancel and restart them, which loses their id, history and waits.

## Decision

The instance moves itself. `IWorkflowInstanceGrain.Migrate(migrationId, plan, dryRun)` validates
the instance against the target version and, when it can move, raises **one** `InstanceMigrated`
event (plus the events of the waits it rewires) in a single `DrainAndRaiseEvents`. Then it swaps
its definition and `WorkflowExecution` for the target's and runs the effects.

- **Plan** (`Fleans.Domain/InstanceMigration/MigrationPlan.cs`): target `ProcessDefinitionId`,
  explicit `ActivityMapping`s (source id → target id; everything else maps to the same id), and
  `UpdateEventTriggers`.
- **Validator** (`InstanceMigrationValidator`): a pure function of state, both definitions and the
  plan, so dry run and migration run the same checks. One rejection code per reason (see the guide).
  Checks run before anything is raised; a rejection changes nothing.
- **Event** `InstanceMigrated(SourceProcessDefinitionId, TargetProcessDefinitionId, Mappings,
  MigrationId)` carries only the id-changing mappings. `Apply` → `WorkflowInstanceState.ApplyMigration`:
  sets `ProcessDefinitionId`, appends a `MigrationRecord`, and renames active entries, timer-cycle
  tracking, conditional watchers, complex-join states and uncompensated compensation-log entries.
  Finished entries keep their ids: history shows what ran. It reads only the event (time from
  `OccurredAt`), so replay reproduces it.
- **Waits** (`WorkflowExecution.Migration.cs`, `MigrationWaits`): for each executing entry, its
  waits (catch event, boundaries, multiple events) in the source and target are matched by key
  (kind + element id + ordinal). Kept unless renamed — or changed and `UpdateEventTriggers` —
  in which case they are cleaned up and registered again; removed ones are cleaned up; added
  ones registered. A renamed or kept-but-re-registered timer keeps its remaining time; a
  non-interrupting boundary that already fired is not re-armed. Cleanups come before
  registrations in the effect list.
- **User tasks**: `UserTaskRedefined` when the assignment changes; the claim stays when the
  claimant still qualifies (assignee / candidate user / every source candidate group kept /
  unrestricted target), else `UserTaskUnclaimed`. `RedefineUserTaskEffect` moves the read-model
  row (log-only on failure).
- **Late callbacks**: a reminder, message, signal or child completion addressed to a pre-migration
  id is translated through `State.Migrations` (`ResolveMigratedActivityId`) when that id is not in
  the current definition; one addressed to a wait the target removed is stale and dropped.

### Idempotency

The migration id is `{requestId}:{instanceId}` (`InstanceMigrationService.MigrationId`).
`Migrate` answers `AlreadyMigrated` without raising when `State.Migrations` holds the id, or when
the last migration already reached the same target with the same renames. Guard 14 in
[`invariants.md`](../conventions/invariants.md#stream-redelivery-idempotency).

### Batch service and API

`InstanceMigrationService` (Application) resolves the instances — explicit ids (≤
`Fleans:Migration:MaxBatchSize`, default 100) or the running instances of a source version,
paged by a keyset on the instance id (`IWorkflowQueryService.GetRunningInstanceIds`, continuation
token = last id) — and calls each grain with `Parallel.ForEachAsync`
(`Fleans:Migration:MaxParallelism`, default 16). Synchronous on purpose: a request is bounded by
the batch size, so no job store is needed. The grain re-validates, so the read model's lag can
only produce a `not-running`/`same-version` rejection, never a wrong move.

REST: `POST /Instances/{id}/migrate`, `POST /Instances/migrate`,
`GET /Definitions/{key}/versions/{from}/migration-preview?to=`. The Admin UI calls the service
directly (grains, not HTTP): `MigrateDialog` from the Workflows page (a version) and the instance
page (one instance). `InstanceJournal` renders the event in the journal and the debugger.

Observability: counter `fleans.instance.migrations{outcome, process.definition.key}` (emitted from
`LogEvent` for `migrated`, so once per real transition; never from `Apply`), span
`fleans.instance.migrate_batch`, EventIds 1170–1174 / 1244 / 4104 / 13094–13095 / 14600–14602.

## Open questions from the issue — answers

1. **Claimed user task whose candidates change**: keep the claim when the claimant still
   qualifies; otherwise unclaim. Group membership is not resolvable in the domain, so "keeps every
   source candidate group" stands in for it.
2. **Call activity children**: not migrated with the parent. A child keeps reporting to its
   parent's element (translated if it was renamed). Migrate the child process separately.
3. **Authorization**: the same as the other admin endpoints (purge, disable): authenticated
   fallback policy and the `admin` rate limit, until roles land (#341).

## Out of scope (v1)

- Re-arming event sub-process listeners: a change to their start events in an open scope is
  rejected (`event-subprocess-changed`).
- Moving a token to an element the target added (that is instance modification).
- An asynchronous bulk job for very large versions: the continuation token pages them instead.
- Per-element running counts in the preview: active entries of running instances are not in the
  read model, so the preview gives the version's total running count.

## Pre-1.0

`WorkflowInstanceState.Migrations` is a new state member; no upgrade path for snapshots written
before it (a fresh database is the upgrade path). The EF read model ignores it, so no migration.
