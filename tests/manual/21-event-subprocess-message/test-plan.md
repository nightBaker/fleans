# Manual Test 21 — Event Sub-Process (Message, Interrupting)

## Scenario

A process waits on a user task. The enclosing scope contains an interrupting
message event sub-process correlated by `orderId`. When a `cancelOrder` message
arrives with a matching correlation key, the user task is cancelled and the
handler runs. The normal `end` event must NOT be reached.

The correlation variable `orderId` must be set **before** `StartWorkflow`
because the message subscription is registered at scope entry and resolves the
correlation key against the variables snapshot at that moment.

## Prerequisites

- Aspire host running: `dotnet run --project Fleans.Aspire`
- `message-event-subprocess.bpmn` available in this folder

## Steps

1. **Deploy** the BPMN: upload `message-event-subprocess.bpmn` via the Web UI
   (Fleans.Web → Deployments → Upload), or
   `POST https://localhost:7140/Workflow/upload-bpmn` with the file.
2. **Start an instance** with an `orderId` variable:
   `POST https://localhost:7140/Workflow/start` with body
   ```json
   {"WorkflowId":"evtSubMessageProcess","Variables":{"orderId":"ORD-123"}}
   ```
   (If the start endpoint does not accept initial variables, create the
   instance via the Web UI and set `orderId=ORD-123` before starting it.)
3. Confirm `userTask` is active in the Web UI. Do NOT complete it.
4. **Deliver a correlated message**:
   `POST https://localhost:7140/Workflow/message`
   ```json
   {"MessageName":"cancelOrder","CorrelationKey":"ORD-123","Variables":{}}
   ```
5. Refresh the instance view.

## Expected Outcomes Checklist

- [ ] `userTask` shows as **Cancelled**.
- [ ] `messageEventSub` event sub-process host shows as **Completed**.
- [ ] `handlerTask` shows as **Completed** inside the event sub-process scope.
- [ ] `handlerEnd` event fired.
- [ ] Workflow instance state is **Completed**.
- [ ] Normal `normalEnd` event is **NOT** reached.
- [ ] A message sent with a different correlation key (e.g. `ORD-999`) does NOT affect this instance.
