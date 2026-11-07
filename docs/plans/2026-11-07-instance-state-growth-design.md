# Instance state growth: keep history, accept the cost

**Status:** proposed · **Date:** 2026-11-07 · **Issue:** [#861](https://github.com/nightBaker/fleans/issues/861)

## Problem

`Entries`, `ConditionSequenceStates`, `CompensationLog` and `TransactionOutcomes` only grow. Every snapshot holds the full state, so total snapshot writes grow quadratically over an instance's life.

These collections are also history that people read:

- A running instance's details are served from the grain (`GetStateSnapshot`), since #875.
- A completed instance's details are written once from its final state.
- The instance page shows compensation entries, including compensated ones.

## Measurement

State size at completion, from the Orleans serializer the snapshot store uses, at `6aac9678`:

| Script + gateway steps | Events | State | `Entries` | `ConditionSequenceStates` |
|---|---|---|---|---|
| 100 | 909 | 41 KB | 31 KB | 10 KB |
| 500 | 4,509 | 203 KB | 154 KB | 49 KB |
| 2,000 | 18,009 | 821 KB | 623 KB | 198 KB |

That is about 410 bytes and 9 events per step. At `SnapshotInterval` = 100, this works out to about 74 MB of cumulative snapshot writes for 2,000 steps and 1.9 GB for 10,000, when the final snapshot is 4.1 MB. Those two figures are calculated, not measured.

The run used a linear chain with almost no variables, because the test silo stubs scripts and conditions. Large variables and compensation snapshots make real instances bigger. Latency under load wasn't measured.

## Decision

Don't prune. Keep all four collections in grain state and document the cost as a known limit.

| Option | Why not |
|---|---|
| A. Project finished entries incrementally and read a running instance's history from the read model | Partly reverses #875 and adds a write path for rare instances |
| B. Rebuild history from the journal when it is needed | Slows every details read and has to be checked against purge and retention (#862, #980) |
| Prune compensated `CompensationLog` entries only | Removes rows the instance page shows |

`TransactionOutcomes` holds one small record per transaction sub-process instance and transaction logic reads it, so it stays.

## Consequences

- Instances of up to a few hundred steps cost tens of kilobytes per snapshot.
- Very long loops stay expensive in memory and write I/O. `WorkflowSnapshots` keeps one row per grain, so disk use doesn't grow with the writes.
- Users can move repeated work into a Call Activity, because a child instance has its own state, or raise `Persistence:SnapshotInterval`.

## Revisit when

An instance's size or snapshot writes cause a real problem. The cheapest first step is writing snapshots less often as state grows. `Persistence:MaxEventsPerLoad` (1000) caps the interval, so that saves up to about 10× and doesn't remove the quadratic growth.
