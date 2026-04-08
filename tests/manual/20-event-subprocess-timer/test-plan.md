# Manual Test 20 — Event Sub-Process (Timer, Interrupting)

## Scenario

A process waits on a user task. The enclosing scope contains an interrupting
timer event sub-process with a `PT5S` duration. When the timer fires, the user
task must be cancelled and the handler script task must run. The normal `end`
event must NOT be reached.

## Prerequisites

- Aspire host running: `dotnet run --project Fleans.Aspire`
- `timer-event-subprocess.bpmn` available in this folder

## Steps

1. **Deploy** the BPMN: upload `timer-event-subprocess.bpmn` via the Web UI
   (Fleans.Web → Deployments → Upload), or
   `POST https://localhost:7140/Workflow/upload-bpmn` with the file.
2. **Start an instance**:
   `POST https://localhost:7140/Workflow/start` with body
   `{"WorkflowId":"evtSubTimerProcess"}`
3. Open the instance in the Web UI and confirm `userTask` is active (claimable).
4. Do NOT claim or complete the user task. Wait ~5 seconds.
5. Refresh the instance view.

## Expected Outcomes Checklist

- [ ] `userTask` shows as **Cancelled** (`IsCancelled = true`).
- [ ] `timerEventSub` event sub-process host shows as **Completed**.
- [ ] `handlerTask` shows as **Completed** inside the event sub-process scope.
- [ ] `handlerEnd` event fired.
- [ ] Workflow instance state is **Completed** (terminal).
- [ ] Normal `normalEnd` event is **NOT** reached.
- [ ] No residual active activities remain.
