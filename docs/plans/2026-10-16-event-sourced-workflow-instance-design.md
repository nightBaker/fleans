# Event-sourced WorkflowInstance: the journal is the only source of truth

## Problem

`WorkflowInstance` is a `JournaledGrain`, but in three places its state still comes from somewhere other than the event journal:

1. **The snapshot is the read model.** `ReadStateFromStorage` loads the `WorkflowInstances` projection row as the snapshot and replays only the events after its `SnapshotVersion`. The projection ignores `UserTasks`, `CompensationLog`, `ActiveCompensationWalks`, `TransactionOutcomes`, `ScopeHostExecutionScopes`, `PendingOperations` and `AppliedOperations`. The `FleanModelConfiguration` comments say these are rebuilt from events on activation, but they aren't, because the events are skipped. After any reactivation they are empty: transaction outcomes vanish, the compensation log is gone, the #657 pending-event queue and dedup ledger are lost, and `RehydrateUserTasks` patches user tasks back in from the query DB.
2. **Replay is not deterministic for time.** `Apply` handlers stamp `CreatedAt`/`ExecutionStartedAt`/`CompletedAt` with `DateTimeOffset.UtcNow`. The events already carry an `OccurredAt` field, but nothing sets it, so it is always `default`.
3. **A commit can be retried twice.** The projection write runs inside `ApplyUpdatesToStorage` after the append. If it throws, Orleans' `CustomStorage` adaptor re-reads the state and retries the same batch at the new version, which appends those events a second time.

A smaller issue: `UserTaskLifecycle.ValidateAndPrepareCompletion` mutates `UserTaskMetadata` directly instead of emitting an event.

## Design

The standard event-sourcing split: the journal is the source of truth, snapshots are a cache, and the projection is a derived read model.

- **Snapshots are a cache.** A dedicated `WorkflowSnapshots` table holds `(GrainId, Version, Payload)`, where `Payload` is the full `WorkflowInstanceState` written with the Orleans serializer. That serializer already round-trips the state across grain calls and `DeepCopier`, so every `[Id]` member survives. A snapshot is taken every `IEventStore.SnapshotInterval` events (`Persistence:SnapshotInterval`, default 100; validated at startup to stay below `MaxEventsPerLoad`). It is skipped while an interleaved durable enqueue has changed state that isn't journaled yet, so a snapshot is always exactly the state at its version. A snapshot read that fails is not worked around: Orleans retries the activation read with backoff. Pre-1.0 there is no fallback for state from older builds (fresh database), and a transient error must not degrade into a full replay that `MaxEventsPerLoad` could reject.
- **The projection is write-only.** `IWorkflowStateProjection` writes the read model and reports the version it has projected (`ProjectedVersion` column). The grain never loads state from it. A failed projection write is retried by a keep-alive grain timer with doubling delays, because the failed write may be the instance's last one. After 8 attempts it gives up, so a write that always fails cannot pin the grain. All projection writes are serialized and never move the projected version backwards. On activation, a projection behind the journal is rewritten in full, which covers a silo crash between an append and its projection write.
- **The append is the commit point.** `ApplyUpdatesToStorage` returns `true` once the append succeeds. Projection and snapshot failures after that are logged and do not throw, so Orleans never retries a committed batch.
- **Timestamps travel in the events.** All journaled events derive from `WorkflowEvent` (an abstract record with `OccurredAt`). `WorkflowExecution.Stamp` (via `Emit`, or the grain's durable enqueue) sets `OccurredAt`, the journal is typed on `WorkflowEvent`, and `Apply` handlers pass it to the state transition methods, which take the timestamp as a parameter. The domain state no longer reads the clock.
- **User-task completion validates without mutating.** The direct `metadata.Complete()` call is removed; the task leaves the state through `UserTaskUnregistered` in the same command. A rejected completion therefore leaves nothing behind. `RehydrateUserTasks` is deleted, because user tasks come from the snapshot and the events.

Not done (KISS / pre-1.0): event upcasting, snapshot schema versioning, and asynchronous projections.

## Upgrade

Pre-1.0, so there is no upgrade path: start from a fresh database. The Postgres migration renames `SnapshotVersion` to `ProjectedVersion` and creates `WorkflowSnapshots`.
