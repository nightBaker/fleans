# Manual Test 23 — Event Sub-Process (Non-Interrupting Timer)

## Scenario

A process waits on a user task. The enclosing scope has a **non-interrupting**
timer event sub-process (`isInterrupting="false"`) with a `PT5S` duration.
When the timer fires, the handler runs **in parallel** with the user task —
the user task is NOT cancelled. After the handler completes, the user task can
still be claimed and completed normally, and the workflow finishes through the
regular `normalEnd` event.

## Prerequisites

- Aspire host running: `dotnet run --project Fleans.Aspire`
- `ni-event-subprocess.bpmn` available in this folder

## Steps

1. **Deploy** the BPMN: upload `ni-event-subprocess.bpmn` via the Web UI, or
   `POST https://localhost:7140/Workflow/upload-bpmn`.
2. **Start an instance**:
   `POST https://localhost:7140/Workflow/start` with body
   `{"WorkflowId":"niEvtSubProcess"}`
3. Confirm `parentTask` is active in the Web UI.
4. Wait ~5 seconds for the timer to fire.
5. Refresh the instance view — `parentTask` must still be active, the handler
   must have run, and `handlerEnd` must have fired.
6. Claim and complete `parentTask` via the Web UI.
7. Refresh the instance view.

## Expected Outcomes Checklist

- [ ] After ~5s, `handlerTask` shows as **Completed** (the non-interrupting
      handler ran in parallel).
- [ ] After ~5s, `parentTask` is **still Active** — it was NOT cancelled.
- [ ] After ~5s, `niTimerEventSub` shows as **Completed**.
- [ ] After claiming and completing `parentTask`, the workflow reaches
      **Completed** via the normal `normalEnd` event.
- [ ] `normalEnd` IS reached (unlike the interrupting variants).
