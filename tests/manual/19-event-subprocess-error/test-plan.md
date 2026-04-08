# Manual Test 19 — Event Sub-Process (Error, Interrupting)

## Scenario

A process runs a script task that throws an unhandled exception (error code 500).
The enclosing scope contains an interrupting error event sub-process that catches
the error and runs a handler script task. The handler must execute, the failing
task must be recorded as failed, and the workflow must terminate via the
event-sub-process path (the normal `normalEnd` event is NOT reached).

## Prerequisites

- Aspire host running: `dotnet run --project Fleans.Aspire`
- `error-event-subprocess.bpmn` available in this folder

## Steps

1. **Deploy** the BPMN: upload `error-event-subprocess.bpmn` via the Web UI
   (Fleans.Web → Deployments → Upload), or
   `POST https://localhost:7140/Workflow/upload-bpmn` with the file.
2. **Start an instance**:
   `POST https://localhost:7140/Workflow/start` with body
   `{"WorkflowId":"evtSubErrorProcess"}`
3. Open the instance in the Web UI and confirm it is running.
4. Wait briefly — the script task throws synchronously, the handler runs,
   and the workflow should reach a terminal state within a second.

## Expected Outcomes Checklist

- [ ] `failingTask` is recorded as **Failed** with error code `500`.
- [ ] `errorEventSub` event sub-process host shows as **Completed**.
- [ ] `handlerTask` shows as **Completed** inside the event sub-process scope.
- [ ] `handlerEnd` event fired.
- [ ] Workflow instance state is **Completed** (terminal).
- [ ] The normal `normalEnd` event is **NOT** reached (no completion entry for it).
- [ ] No residual active activities remain.
