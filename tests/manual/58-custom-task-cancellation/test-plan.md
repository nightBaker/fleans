# Manual Test Plan #58 — Custom-Task Cancellation on Grain Deactivation

Verifies the cancellation contract added by #568: `CustomTaskHandlerBase` threads a grain-lifetime `CancellationToken` into the plugin's `ExecuteAsync`, and an `OperationCanceledException` triggered by grain deactivation re-throws (no `FailActivity` call) so the stream provider redelivers the event after reactivation.

The expected end-state after deactivation is **not** "activity failed" and **not** "in-flight event lost" — it is "activity remains live; stream redelivers; the new handler activation completes it on the next silo turn".

## Prerequisites

- Aspire stack stopped.
- Clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`).
- A long-blocking HTTP endpoint at a known URL. The simplest setup: run a tiny `dotnet new web` listener on `:5555` whose `GET /sleep` does `await Task.Delay(Timeout.Infinite, ct)`. Any equivalent works (Python `python3 -c "...sleep..."`, etc.) — what matters is "blocks until the caller cancels".

## Step 1 — RestCaller plugin invokes the long endpoint

1. Start the Aspire stack: `dotnet run --project Fleans.Aspire` from `src/Fleans/`.
2. Open the Web UI at `https://localhost:7124`. Navigate to `/editor`.
3. Author a BPMN with one `serviceTask type="rest-call"` configured to:
   - `url = "http://host.docker.internal:5555/sleep"` (or the equivalent for your host)
   - `method = "GET"`
   - `timeoutSec = 600` (long enough to outlive the test)
4. Deploy + start the workflow.
5. Verify the workflow instance is in **Running** state and the custom-task activity is **Active** — the HTTP call should be blocked.

## Step 2 — Trigger silo deactivation mid-call

Two equivalent paths; pick one.

**Path A (Aspire-level):** Stop the `fleans-core` resource from the Aspire dashboard (Stop button on the row). Wait until the dashboard reports `Stopped`. Then click Start to bring it back.

**Path B (process-level):** From a terminal, `ps aux | grep Fleans.Api`. `kill <PID>`. Wait for Aspire to auto-restart it.

## Step 3 — Verify the redelivery contract

Within ~1 s of the silo restart, check:

1. **Workflow instance is still Running.** The state-snapshot endpoint at `GET https://localhost:7140/Workflow/instances/{id}/state` should show the custom-task activity still **Active** (not Failed).
2. **The custom-task activity has NOT been marked failed.** Specifically, no row for this activity appears in any "Failed activities" UI projection, and the workflow state's failed-activities collection is empty for this `ActivityInstanceId`.
3. **The silo console log contains exactly one `LogPluginCancelledOnDeactivation` line** (EventId 4050) with `TaskType=rest-call` and the activity id from your workflow:
   ```
   info: Fleans.Worker.CustomTasks.CustomTaskHandlerBase[4050]
         Custom-task plugin rest-call cancelled on grain deactivation for activity <id> — stream provider will redeliver after reactivation
   ```
4. **The new silo activation receives the redelivered event.** The silo's log should show a fresh `LogHandling` (EventId 4030) line for the same activity id within ~1 s of restart. If your long endpoint is still blocking, the new activation will also block — the test passes once the redelivery is observed, regardless of whether the second attempt completes.

> **PASS criteria:** all four observations hold.
> **FAIL** (file a bug) if any of: (a) workflow shows Failed for the custom-task activity, (b) `LogPluginCancelledOnDeactivation` is missing or appears more than once for a single deactivation, (c) the redelivered event never reaches a new activation, (d) `FailActivity` was called (visible as `LogExecuteFailed` EventId 4031 followed by FailActivity in the WorkflowInstance journal).

## Step 4 — Plugin-internal OCE still fails the activity

Distinguishes the "our cancellation" path (Steps 1–3) from "plugin's own timeout" — they must produce different outcomes.

1. Re-deploy the same BPMN with `timeoutSec = 2` (short enough to fire before any deactivation).
2. Start the workflow. The custom-task activity sends a GET to the long-blocking endpoint.
3. Wait 3 s.
4. **Verify the workflow instance is Failed** (or the activity is Failed with code `504`, per the RestCaller plugin's timeout-to-`CustomTaskFailedActivityException` translation), NOT cancelled-and-redelivered.
5. The silo log shows `LogExecuteFailed` (EventId 4031) **without** a preceding `LogPluginCancelledOnDeactivation` — the plugin's internal timeout fired, the `when` filter in the base class sees `_grainLifetimeCts.IsCancellationRequested == false`, and the OCE falls through to the regular `FailActivity` path.

> **PASS criteria:** workflow is Failed, `LogExecuteFailed` present, `LogPluginCancelledOnDeactivation` absent.

## Cleanup

```bash
# stop the long-blocking endpoint if still running
# delete the dev DB if you ran this immediately before another test plan
```

## Out of scope (covered elsewhere)

- Plugin authors who **ignore** the token (block forever on un-cancellable I/O) — Orleans force-kills at the hard-timeout boundary; the stream still redelivers on reactivation. Demonstrating this requires a deliberately broken plugin and is covered by the deferred integration test in `Fleans.Application.Tests` (when WorkflowTestBase grows a plugin-registration hook).
- Streaming-provider-specific redelivery latency. Redis Streams uses consumer-group XACK semantics; AzureQueue and Kafka have their own delivery contracts. This plan validates against the default (Redis) — equivalent runs against `FLEANS_STREAMING_PROVIDER=AzureQueue` / `Kafka` are reasonable follow-up steps but not required to mark this entry PASS.
