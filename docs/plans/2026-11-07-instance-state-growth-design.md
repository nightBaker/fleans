# Instance state growth: prune finished history at snapshot time

**Status:** implemented · **Date:** 2026-11-07 · **Issues:** [#861](https://github.com/nightBaker/fleans/issues/861), [#1050](https://github.com/nightBaker/fleans/issues/1050)

This replaces the first decision for #861, which was to keep all history in state and accept the cost.

## Problem

`Entries` and `ConditionSequenceStates` only grew, and every snapshot held them in full. Total snapshot writes therefore grew quadratically over an instance's life.

State size at completion, from the Orleans serializer the snapshot store uses, at `6aac9678`:

| Script + gateway steps | Events | State | `Entries` | `ConditionSequenceStates` |
|---|---|---|---|---|
| 100 | 909 | 41 KB | 31 KB | 10 KB |
| 500 | 4,509 | 203 KB | 154 KB | 49 KB |
| 2,000 | 18,009 | 821 KB | 623 KB | 198 KB |

That is about 410 bytes and 9 events per step. At `SnapshotInterval` = 100, this works out to about 74 MB of cumulative snapshot writes for 2,000 steps and 1.9 GB for 10,000. Those two figures are calculated, not measured.

## Decision

Remove finished history from state just before a snapshot is written. It stays in the journal, so a history read rebuilds it from there.

- **One method, no new event, nothing in `Apply`.** `WorkflowExecution.PruneFinishedHistory` runs in `ApplyUpdatesToStorage`, before `TryWriteSnapshot`. A full replay does not prune, which is what makes history reads possible.
- **A pruned entry behaves like a completed one.** There is no completed-ID set: a callback for a missing entry is stale, and the grain logs a completion it drops this way (1149).
- **No new table.** Where history comes from:

| Instance | Source |
|---|---|
| Running, nothing pruned | The grain's state |
| Running, entries pruned | `WorkflowQueryService` replays the journal, told to by `InstanceStateSnapshot.HistoryPruned` from the grain |
| Completed | The read model, written once at completion after the projector replays the journal |

Paged journal reads (`IEventStore.ReadEventPageAsync`) serve those replays and the admin UI's debug mode, because `ReadEventsAsync` refuses to read past `Persistence:MaxEventsPerLoad`. The cap stays for the activation path, where a long tail means snapshotting is broken.

### What is kept

An entry is removed only when it is finished and no rule below keeps it. Each rule protects a reader found by auditing every `GetEntry`, `FindEntry`, `Entries`, `GetEntriesInScope` and `GetCompletedActivities` call.

| Kept | Read by |
|---|---|
| Active entries | Everything |
| Failed entries | `TryFailStalledWorkflow`, transaction hazard handling, `TrimEndedDetachedScopes` |
| Completed root end events | `TryDeferredWorkflowCompletion` |
| Everything inside a scope still open, at any depth | Scope completion, transaction and compensation walks; `FindDescendantHazardTx` and `IsDescendantOfScope` climb through nested scopes that already closed |
| A scope whose compensation walk still runs, with its host | `GetScopeDefinition` and the walk's parent-variables lookup: a cancelled Transaction's host completes before its walk ends |
| An open fork's entry and the entries on its tokens | `ResolveForkJoinTransition`, `AllExpectedTokensArrived`, `TrimEndedDetachedScopes` |
| A complex join's first entry | `CompleteComplexGatewayJoin` |
| A running walk's thrower and current handler | `AdvanceCompensationWalk`, `AdvanceCompensationWalkIfHandlerCompleted` |

Condition results are removed when their gateway has finished without an error, or is gone.

`CompensationLog` and `TransactionOutcomes` are not pruned: transaction logic reads them and the instance page shows compensated entries.

### What the issue expected and the audit changed

- **No Call Activity rule.** The issue kept a Call Activity whose child can still call back. A child's callback goes through `GetFirstActive`, and `AppliedOperations` deduplicates, so nothing reads the finished entry. Its only lookup by child id had no callers and was deleted.
- **Pruning is limited to settled drains.** The issue hoped transitions were always resolved before a drain, and they are not. A message or signal subscription's effect calls `PersistStateAsync` in the middle of an iteration, and an `[AlwaysInterleave]` durable enqueue appends at any await. An activity can have completed and still wait for `ComputeTransitionsForEntries`, which would throw on a removed entry. So only `DrainSettledEvents` allows pruning (the execution loop's periodic flush and the drains at the end of `ProcessPendingEvents`). The other snapshots keep the history until a later one removes it.
- **"Open scope" is transitive,** because the hazard and descendant walks climb through closed nested scopes.

### Risks, answered

- **Snapshot equality.** A snapshot is now the state at its version less pruned history. `InstanceHistoryUnprunedTests` holds behaviour with snapshots far apart to the same results, and the test silo's `SnapshotInterval` is 1, so the whole integration suite runs with pruning at every settled append.
- **Engine decisions.** The suite found one decision that depended on history: `AllExpectedTokensArrived` read the first arrived token, which on a loop's second iteration belonged to a removed fork, so the join never fired. That bug predates this work and would have made behaviour depend on the interval. The join now looks for an open fork among the arrived tokens.
- **A failed snapshot write.** `HasPrunedHistory` is set by the prune, before the write. A failed write leaves the flag on the live state, so details reads go to the journal. An activation from the older snapshot simply has the history again.
- **Replay cost on details reads.** See below. It applies only to running instances that have written a snapshot and pruned something.

## Measurement

A task and an exclusive gateway in a root-level loop, driven through the grain, 8 events per step. A development machine, shared-cache in-memory SQLite, almost no variables, Debug build. Each replay and details read is the median of 7. Where two runs disagree the range is shown.

| Steps | Events | Snapshot | Replay from the start | Details read | Debug-mode load |
|---|---|---|---|---|---|
| 13 | 111 | 304 B | 0.7–0.8 ms | 0.4–1.1 ms | 4–7 ms |
| 63 | 511 | 304 B | 1.2–1.3 ms | 1.8–2.5 ms | 2–3 ms |
| 125 | 1,007 | 304 B | 2.1–2.3 ms | 2.5–3.2 ms | 4–19 ms |
| 500 | 4,007 | 304 B | 10–14 ms | 12–15 ms | 26–33 ms |
| 1,000 | 8,007 | 304 B | 23–37 ms | 26–40 ms | 44–90 ms |
| 2,250 | 18,007 | 304 B | 62–142 ms | 65–141 ms | 134–222 ms |

The snapshot no longer grows with the step count: 304 bytes at 18,007 events, against 821 KB before. Cumulative snapshot writes for that run are about 53 KB at interval 100, against about 74 MB. A real instance's snapshot also holds its variables and active entries, so 304 bytes is the size of an empty one, not a typical one. Real reads cost more on a networked database.

### Default `SnapshotInterval`: 500

| Interval | Snapshot writes for 18,007 events | Tail replay on activation, at most about | Step time once warm |
|---|---|---|---|
| 100 | 173 | 0.8 ms | 0.33–0.38 ms |
| 500 | 35 | 1.3 ms | 0.34–0.44 ms |

Raising the interval adds about half a millisecond to an activation and removes four writes in five. The step times overlap and are too noisy to separate. It also means an instance of up to about 60 steps never writes a snapshot, so it never pays for a journal replay on a details read, and the projector reads its journal in one read. Where history cannot be pruned, a loop inside a scope writes a fifth as many of its growing snapshots.

`MaxEventsPerLoad` stays 1000. Its margin over the interval falls from 900 to 500 events, which matters only when snapshot writes keep failing (logged as 1131) or a very large batch lands just before a crash. The limit can be raised if either happens.

## Consequences

- A long root-level loop has a constant snapshot.
- A loop inside an open scope, or on a branch of an unjoined fork, still grows until that scope closes or the fork joins. Moving the work into a Call Activity remains the answer for those.
- A details read of a very long running instance costs a journal replay in the query service, off the grain's turns. At 18,000 events that is 65–140 ms here, per read. If polling such instances becomes a problem, the cheapest fix is caching the replayed state by version and reading only the tail.
- New code that reads a finished entry has to extend the keep rules: [`invariants.md`](../conventions/invariants.md#a-new-read-of-a-finished-entry-extends-the-pruning-rules).
- Pre-1.0: a fresh database is the upgrade path.
