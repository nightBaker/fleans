# 69 — Unhandled activity failure fails the instance (#762)

When an activity fails and **nothing handles the error** (no error boundary event, no error event sub-process), and that failure leaves the instance with **no active activities**, the instance must terminate as **Failed**. Before #762 such an instance silently sat in *Running* forever with `activeActivityIds=[]`, `isCompleted=false` — the token was dropped and nothing surfaced the failure.

Automated coverage: `src/Fleans/Fleans.Application.Tests/UnhandledActivityFailureTests.cs` (unhandled upstream failure, unhandled custom-task failure, successful completion, boundary-handled failure, partial failure while another branch is still active). E2E: `MessageEventTests.MessageCatchMissingCorrelation_RegistrationFails_WorkflowMarkedFailed` waits for `isFailed=true`.

## Prereqs

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`). Api at `https://localhost:7140`, Web UI at the URL shown in the Aspire dashboard.
- A fresh or existing SQLite dev DB both work — on an older dev DB the SQLite schema initializer adds the new `WorkflowInstances.IsFailed` column at startup.

## Scenario A — unhandled failure fails the instance

1. **Deploy** `unhandled-script-failure.bpmn` (Web UI → *Process Definitions* → *Upload BPMN*, or the editor). Deploy succeeds.
2. **Start** an instance:
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/start \
     -H "Content-Type: application/json" \
     -d '{"WorkflowId":"unhandled-script-failure"}'
   ```
3. **Check state** (`curl -k https://localhost:7140/Workflow/instances/<id>/state`). Expect within a few seconds:
   - `isFailed: true`, `isCompleted: true`, `isCancelled: false`, `completedAt` set
   - `activeActivityIds: []`
   - `completedActivities` contains `boom` with `errorState.code = "500"` and a DynamicExpresso parse message
   - `after` and `end` were **never** reached
4. **Web UI:** *Process Instances* list and the instance detail page show a red **Failed** badge (not *Running*, not *Completed*). The `boom` activity shows its error.
5. **Logs** (Aspire dashboard → `api` / `core` resource structured logs): one **Error** entry, EventId **1120**, `Workflow failed: unhandled failure of activity boom (...) left no active activities. ErrorCode=500, ...`.
6. **Metrics** (optional): `fleans.workflow.terminated` increments with `result="failed"`.

## Scenario B — handled failure still completes

1. **Deploy** `handled-script-failure.bpmn` and start an instance (`{"WorkflowId":"handled-script-failure"}`).
2. Expect `isCompleted: true`, **`isFailed: false`**, `boom` carries an `errorState`, `recover` and `recoveredEnd` are completed, `end` is not. Badge: **Completed**.

## Scenario C — user task failed without a boundary

1. Deploy `tests/manual/45-user-task-fail-cancel/user-task-simple.bpmn` and start it; the user task waits in *Active*.
2. Find its `activityInstanceId` (`GET https://localhost:7140/UserTasks`) and fail it:
   ```bash
   curl -k -X POST https://localhost:7140/UserTasks/<activityInstanceId>/fail \
     -H "Content-Type: application/json" \
     -d '{"ErrorCode":"E_REJECTED","ErrorMessage":"User rejected the task"}'
   ```
3. Expect `isFailed: true`, the user task's `errorState.code = "E_REJECTED"`, badge **Failed**.

## Out of scope

- A failure inside a sub-process with no boundary keeps the sub-process host active (the instance stays *Running* with the host visible) — unchanged by #762.
- A failure on one parallel branch while another branch is still active does not fail the instance immediately; the instance fails only once no active activities remain (if the other branch reaches a root end event first, the instance completes, as before).

## Verdict

Pass / Fail. If failing, capture the `/state` JSON, the instance's Web UI badge, and any EventId 1120 log lines.
