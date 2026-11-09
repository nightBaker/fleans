# Manual Test Plan #58 — Custom-Task Cancellation on a Lost Worker

Verifies the cancellation contract: a task whose worker goes away is not failed. The worker's `CancellationToken` fires, nothing is reported, the dispatcher rethrows, and the stream delivers the task again.

The expected end-state after the worker drops is **not** "activity failed" and **not** "in-flight event lost" — it is "activity remains live; stream redelivers; the next connected worker gets the task".

## Prerequisites

- Aspire stack stopped.
- Clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`).
- A checkout of [nightBaker/fleans-plugins](https://github.com/nightBaker/fleans-plugins) with its REST Caller host built: `dotnet build src/Fleans.RestCaller.Host` (Aspire runs a path-based project without building it). The engine images bundle no plugin, so this worker is what serves `rest-call`.
- A long-blocking HTTP endpoint at a known URL. The simplest setup: run a tiny `dotnet new web` listener on `:5555` whose `GET /sleep` does `await Task.Delay(Timeout.Infinite, ct)`. Any equivalent works — what matters is "blocks until the caller cancels".

## Step 1 — A worker invokes the long endpoint

1. Start the Aspire stack with the REST Caller host attached, from `src/Fleans/`: `FLEANS_EXTERNAL_WORKER_PROJECT=<abs path>/fleans-plugins/src/Fleans.RestCaller.Host/Fleans.RestCaller.Host.csproj dotnet run --project Fleans.Aspire`. It runs as the `fleans-external-worker` resource.
2. Open the Web UI at `https://localhost:7124`. Navigate to `/editor`.
3. Author a BPMN with one `serviceTask type="rest-call"` configured to:
   - `url = "http://localhost:5555/sleep"` (or the equivalent for your host)
   - `method = "GET"`
   - `timeoutSec = 120`
4. Deploy + start the workflow.
5. Verify the workflow instance is **Running** and the custom-task activity is **Active** — the HTTP call should be blocked.

## Step 2 — Drop the worker mid-call

Stop the `fleans-external-worker` resource from the Aspire dashboard. Wait for `Stopped`, then click Start to bring it back within the stream delivery window (6 min).

## Step 3 — Verify the redelivery contract

1. **Workflow instance is still Running.** `GET https://localhost:7140/Instances/{id}/state` shows the activity still **Active** (not Failed).
2. **The activity has NOT been marked failed.** No failed-activity row for this `ActivityInstanceId`.
3. **`fleans-core`'s log shows the loss:** `The worker running the external task for activity <id> … disconnected` (EventId 14504). A hard kill may show it only after the lease expires, or as `No outcome … within` (14505) after `ExecutionTimeout`.
4. **The task is delivered again.** A fresh `Dispatching external task for activity <id>` (14500) follows, and the restarted worker receives it; `No connected worker accepted` (14502) may appear while it is away. If the endpoint still blocks, the new attempt blocks too — the test passes once the redelivery is observed.

> **PASS criteria:** all four observations hold.
> **FAIL** (file a bug) if: (a) the workflow shows Failed for the activity, (b) the redelivered event never reaches a worker, (c) `FailActivity` was called (`External task failed for activity`, EventId 14510).

## Step 4 — A handler's own timeout still fails the activity

Distinguishes the "engine or connection cancelled" path (Steps 1–3) from "handler's own timeout" — they must produce different outcomes.

1. Re-deploy the same BPMN with `timeoutSec = 2`.
2. Start the workflow and wait 3 s.
3. **Verify the workflow instance is Failed** (or the activity is Failed with code `504`, the REST Caller's timeout code), NOT cancelled-and-redelivered.
4. `fleans-core`'s log shows `External task failed for activity` (14510) with no preceding 14504.

> **PASS criteria:** workflow is Failed, 14510 present, 14504 absent.

## Cleanup

```bash
# stop the long-blocking endpoint if still running
# delete the dev DB if you ran this immediately before another test plan
```

## Step 5 — Boundary timer interrupts a long-running custom task (automated)

Fixture: `probe-boundary-timer.bpmn` (`e2e-probe` task with a PT3S interrupting timer; start with `delayMs=10000`, `callbackUrl`, `probeKey`). Automated as `SplitRoleTopologyTests.BoundaryTimer_InterruptsLongRunningCustomTask_TimeoutPathTaken_LateCompletionDropped` (`E2E-SplitRoles`).

> **PASS criteria:** the instance completes via `timeoutPath` / `endTimeout`; `normalPath` never runs; after the worker finishes, its late report is dropped (no `probeWorker` variable).
> **Known gaps:** the handler's `CancellationToken` is NOT cancelled on interrupt — the probe runs to completion (#786, spec `BoundaryTimer_InterruptingCustomTask_CancelsHandlerToken` is `[Ignore]`d). The timer path currently takes ~30s to proceed because of a `WorkflowInstance` ↔ `TimerCallbackGrain` call cycle (#785).

## Out of scope (covered elsewhere)

- Handlers that **ignore** the token keep running in the worker process; their late report gets `FAILED_PRECONDITION` and is dropped by the SDK. Covered by `GrpcExternalTaskWorkerTests`.
- Streaming-provider-specific redelivery latency. This plan validates against the default (Redis); equivalent runs against `FLEANS_STREAMING_PROVIDER=AzureQueue` / `Kafka` are reasonable follow-up steps but not required to mark this entry PASS.
