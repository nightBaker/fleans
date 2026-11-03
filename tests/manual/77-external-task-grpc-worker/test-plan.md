# Manual Test Plan #77 — External-task gRPC worker

Verifies that a service task whose type is listed in `Fleans:ExternalTasks:TaskTypes` is delivered to an external worker connected over gRPC (`external_task_worker.proto`, `Fleans.ExternalTasks.Sdk`), that the worker shows up in the custom-task catalog while connected, and that a task is delivered again when its worker drops.

## Prerequisites
- Aspire stack with the test worker: from `src/Fleans/`,
  `FLEANS_EXTERNAL_WORKER_PROJECT=$PWD/Fleans.E2E.ExternalWorker/Fleans.E2E.ExternalWorker.csproj dotnet run --project Fleans.Aspire`.
  The AppHost then sets `Fleans__ExternalTasks__TaskTypes__0=e2e-ext-probe` on `fleans-core` and starts `fleans-external-worker` with only `Fleans__ExternalTaskWorker__EngineUrl`.
- Clean dev DB.
- API origin: `https://localhost:7140`.
- Fixture: `external-probe.bpmn` (this folder).

## Scenario 1 — The worker is catalogued and runs the task

1. `curl -k https://localhost:7140/custom-tasks/e2e-ext-probe` — expect 200, `displayName` `E2E External Probe`, `siloNames` holding one worker name (`<machine>-<pid>`), not a silo name.
2. Deploy `external-probe.bpmn` and start `external-probe`.
3. Poll the instance state — expect it completed, with `probeWorker` equal to the catalog's worker name.
4. Core logs: `Dispatching external task for activity probe` (EventId 14500) and the assignment (14501).

## Scenario 2 — Worker drops, task waits and is redelivered

1. Stop `fleans-external-worker` in the Aspire dashboard. Within `Fleans:ExternalTasks:WorkerTimeout` (45 s) `/custom-tasks/e2e-ext-probe` returns 404 (on a clean disconnect immediately).
2. Start an instance — it stays Active on `probe`; core logs `No connected worker accepted the external task` (14502) on each redelivery.
3. Start the worker again within the stream delivery window (6 min) — the instance completes.

## Scenario 3 — Unrouted types still go to plugins

1. Deploy `tests/manual/37-custom-task-framework/stub-custom-task.bpmn` and start it — the task stays Active (no plugin), and no 145xx dispatcher log appears for it.

## Pass criteria
All scenarios behave as described; no worker row survives in the catalog after the worker stops.
