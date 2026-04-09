# Manual Test 22 — Event Sub-Process (Signal, Interrupting)

## Scenario

A process waits on a user task. The enclosing scope contains an interrupting
signal event sub-process listening for `cancelEverything`. When the signal is
broadcast, the user task is cancelled and the handler runs. The normal `end`
event must NOT be reached. Because signals fan-out, every running instance
listening on `cancelEverything` should be interrupted by a single broadcast.

## Prerequisites

- Aspire host running: `dotnet run --project Fleans.Aspire`
- `signal-event-subprocess.bpmn` available in this folder

## Steps

1. **Deploy** the BPMN: upload `signal-event-subprocess.bpmn` via the Web UI,
   or `POST https://localhost:7140/Workflow/upload-bpmn`.
2. **Start two instances** of `evtSubSignalProcess`:
   `POST https://localhost:7140/Workflow/start` with body
   `{"WorkflowId":"evtSubSignalProcess"}` (repeat).
3. Confirm both instances have `userTask` active in the Web UI. Do not complete them.
4. **Broadcast the signal**:
   `POST https://localhost:7140/Workflow/signal` with body
   `{"SignalName":"cancelEverything"}`
5. Refresh both instance views.

## Expected Outcomes Checklist

- [ ] Both instances have `userTask` marked **Cancelled**.
- [ ] Both instances have `signalEventSub` marked **Completed**.
- [ ] Both instances have `handlerTask` marked **Completed**.
- [ ] Both instances reach **Completed** terminal state.
- [ ] The normal `normalEnd` event is not reached in either instance.
- [ ] Each instance fires its handler exactly once (no duplicate handler entries).
