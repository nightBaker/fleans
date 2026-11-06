# Manual Test Plan #77 — External-task gRPC worker

Verifies that a custom task is delivered to a worker connected over gRPC (`external_task_worker.proto`, `Fleans.ExternalTasks.Sdk`), that the worker shows up in the custom-task catalog while connected, that a task is delivered again when its worker drops, and that a `FailTask` code reaches an error boundary.

## Prerequisites
- Aspire stack with the test worker: from `src/Fleans/`,
  `FLEANS_EXTERNAL_WORKER_PROJECT=$PWD/Fleans.E2E.ExternalWorker/Fleans.E2E.ExternalWorker.csproj dotnet run --project Fleans.Aspire`.
  The AppHost then starts `fleans-external-worker` with only `Fleans__ExternalTaskWorker__EngineUrl`.
- Clean dev DB.
- API origin: `https://localhost:7140`.
- Fixtures: `external-probe.bpmn`, `external-probe-error-boundary.bpmn` (this folder).

## Scenario 1 — The worker is catalogued and runs the task

1. `curl -k https://localhost:7140/custom-tasks/e2e-ext-probe` — expect 200, `displayName` `E2E External Probe`, `siloNames` holding one worker name (`<machine>-<pid>`), not a silo name.
2. Deploy `external-probe.bpmn` and start `external-probe`.
3. Poll the instance state — expect it completed, with `probeWorker` equal to the catalog's worker name.
4. Core logs: `Dispatching external task for activity probe` (EventId 14500) and the assignment (14501).

## Scenario 2 — Worker drops, task waits and is redelivered

1. Stop `fleans-external-worker` in the Aspire dashboard. Within `Fleans:ExternalTasks:WorkerTimeout` (45 s) `/custom-tasks/e2e-ext-probe` returns 404 (on a clean disconnect immediately).
2. Start an instance — it stays Active on `probe`; core logs `No connected worker accepted the external task` (14502) on each redelivery.
3. Start the worker again within the stream delivery window (6 min) — the instance completes.

## Scenario 3 — A type no worker serves waits

1. Deploy `tests/manual/37-custom-task-framework/stub-custom-task.bpmn` and start it — the task stays Active, and core logs 14502 on each redelivery.
2. `POST /Execution/complete-activity` with the activity instance id — the instance moves on.

The wait is bounded: past `Fleans:Streaming:MaxEventDeliveryTime` (6 min) the stream skips the event without any log, and the activity stays Active until reported over REST. A worker connecting after that does not get the task.

## Scenario 4 — FailTask reaches an error boundary (automated)

Fixture: `external-probe-error-boundary.bpmn`. Automated by `ExternalTaskWorkerTests.FailTask_Code_IsCaughtByTheMatchingErrorBoundary` and `FailTask_CodeNoBoundaryDeclares_FailsTheInstance`.

1. Deploy it, then start `external-probe-error-boundary` with `{"failCode":"PROBE_DECLINED"}` — the instance completes through `recover` and `recoveredEnd`, `end` never runs, `isFailed` is false, and `recovered` is true.
2. Start it with `{"failCode":"SOME_OTHER_CODE"}` — the instance fails; `recover`, `recoveredEnd` and `end` never run; core logs `External task failed for activity probe` (14510).

## Deferred tasks and wildcards

No manual scenario: `ExternalTaskHandler.Deferred` and `scheme:*` subscriptions are covered by `GrpcExternalTaskWorkerTests` (the deferred task stays active past `ExecutionTimeout` and is settled over REST; exact claims win over a wildcard; a wildcard is not catalogued).

## Pass criteria
All scenarios behave as described; no worker row survives in the catalog after the worker stops.
