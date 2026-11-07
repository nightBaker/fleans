# Read-model projector capacity

**Status:** proposed · **Date:** 2026-11-07 · **Issue:** [#1011](https://github.com/nightBaker/fleans/issues/1011)

## Problem

`signal-fanout` at 100 000 subscribers (#976 / PR #998) delivers the signal to every instance, but
`linear-10` running alongside degrades: 452 of 2 399 starts answered 429, duration p99 0.74 s → 24 s.

The cause is the read-model projector (`WorkflowInstanceProjector`, #861), not signal delivery:

- **CPU contention.** The projector carries `[WorkerPlacement]` and runs on the `worker-` /
  `combined-` silos next to `ScriptExecutorGrain`. Orleans has no priorities between grains of one
  silo, so 100 000 completions (journal replay + read-model write each) took the single worker to
  500–730 % CPU and starved `linear-10`'s scripts. Adding worker replicas only dilutes this: the
  projectors and the scripts spread over the same silos, so every replica still gets its share of
  the burst.
- **Memory per waiting instance.** A projector activation is released right after a projection
  only when the instance is completed (`if (state.IsCompleted) DeactivateOnIdle();`). A projector
  of a waiting instance stays until Orleans' default idle collection (`WorkflowInstance` has a
  2-minute `ClassSpecificCollectionAge`, the projector has none). The default 512 MB worker runs
  out of memory at ~10 000 parked instances; the 100k run needed 6 GB.

## Decision

Three steps, in this order. Each one ships on its own.

### Step 1 — release the projector of a waiting instance after it projects

Deactivate the projector after every successful projection, not only after a completed one:
drop the `state.IsCompleted` condition in `WorkflowInstanceProjector.OnNextAsync`.

*Why this is cheap:* the projector holds no state but `_projectedVersion` and replays the journal
on every notification anyway (`JournalReplay.LoadAsync`). After a reactivation the only extra cost
is the activation itself; a redelivered or out-of-date notification is still a no-op thanks to the
`ProjectedVersion` guard on the row. A running instance publishes only when its
`WorkflowInstanceRow` changes (#878) — a handful of notifications per instance — so reactivation
churn stays small.

*Lost:* coalescing of a burst across a deactivation (a queued notification may reactivate the
projector and project again; the write guard keeps the row correct). If measurement shows real
churn, fall back to a short `ClassSpecificCollectionAge` for the projector instead.

Tests (Orleans.TestingHost, AAA):
- after an instance parks in a waiting activity and its row is projected, no
  `WorkflowInstanceProjector` activation for it remains;
- a later notification (signal → completion) reactivates the projector and the row and details
  reach the completed state; the same for a failing instance.

Docs: `persistence.md` (projector section), `placement-and-roles.md` (memory sizing note).

### Step 2 — optional `Projector` silo role

A dedicated silo for projection, so a completion burst cannot take CPU, memory or Npgsql
connections away from script execution. Opt-in: without a projector silo nothing changes.

- **Role.** `Fleans.WorkerHost` accepts `Fleans:Role=Projector` (silo prefix `projector-`; with
  `Fleans:Kubernetes:Enabled` the pod name must start with it). Same `fleans-worker` image — no new
  project, Dockerfile or image.
- **Placement filter.** `[ProjectorPlacement]` (strategy + attribute in
  `Fleans.Application.Abstractions/Placement/`, director in `Fleans.Worker.Placement`) replaces
  `[WorkerPlacement]` on `WorkflowInstanceProjector`. It is a placement *filter*, like
  `[WorkerPlacement]`, and narrows the candidates to `projector-` silos; with none it falls back to
  `worker-` / `combined-`, and with none of those to every compatible silo (EventId 11302
  Warning, placement range in `observability-eventids.md`).
- **Registration.** `AddWorkerPlacementFilter()` registers the new filter too, so both silo hosts
  (`Fleans.Api`, `Fleans.WorkerHost`) pick it up without changes. Orleans throws
  `KeyNotFoundException` on a silo that places a grain with an unregistered filter, so every silo
  must have it — plugin silos are retired, so there is no external host to keep in step.
- **Isolation stays one-way.** `WorkerPlacementFilterDirector.HasWorkerRole` keeps rejecting
  anything but `worker-` / `combined-`, so scripts and conditions never land on a projector silo.
  `WorkflowInstance` and the other `[CorePlacement]` grains stay on `core-` / `combined-`.
- **Deployments.** Helm: an optional `projector.*` Deployment (off by default, values shaped like
  `worker.*`, including autoscaling). Load stack: a `fleans-projector` service in
  `tests/load/docker-compose.yml`, with its own memory limit and Npgsql pool sizes; the worker keeps
  its 512 MB. Aspire: `FLEANS_SPLIT_ROLES=true` may add it in dev; publish mode only if it is cheap.

Tests: extend `ProjectorPlacementIntegrationTests` with a `core-` + `worker-` + `projector-`
cluster (projector on the projector silo, scripts still on the worker silo), and keep the existing
`core-` + `worker-` case as the fallback test.

Docs: `placement-and-roles.md` (role table, the "There is no projector role" line, trade-off), the
website's deployment / Helm pages, chart `values.yaml` comments.

### Step 3 — measure on the load stack

Run `signal-fanout` with 100 000 subscribers plus `linear-10` in three layouts and record start
rate, 429 count and duration p99 of `linear-10`, projection lag, and CPU / memory of every silo:

| Layout | Expectation |
|---|---|
| baseline: 1 worker, after step 1 only | memory fixed; `linear-10` still degrades |
| 2 workers, no projector silo | less degradation, not within 10 % |
| 1 worker + 1 projector silo | `linear-10` within 10 % of its baseline; read model may lag, but catches up |

Results go into the step 2 PR and `tests/load/README.md` (sizing for the projector silo). If the
projector silo cannot keep up on its own, the issue's optional signal-delivery pace limit becomes
the next step.

## Out of scope

- Carrying `WorkflowInstanceRow` in `InstanceAdvanced` so running-instance notifications skip the
  journal read — separate optimisation, only after profiling shows the replay matters.
- Signal delivery pace limit (pages per second) — only if step 3 shows it is needed.

## Done when

`signal-fanout` with 100 000 subscribers delivers to everyone and `linear-10` keeps its start rate
and p99 (within 10 %) on the load stack, with the default worker memory or a documented sizing.
