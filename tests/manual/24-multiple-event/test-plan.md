# Manual Test 24 — Multiple Event (Catch, Throw, Boundary)

## Scenario A — Multiple Intermediate Catch (Message or Signal)

A process sets a correlation variable then waits at a **multiple intermediate
catch event** with two definitions: a message (`paymentReceived`) and a signal
(`manualOverride`). Whichever fires first completes the catch; the other
subscription is cancelled automatically.

### Prerequisites

- Aspire host running: `dotnet run --project Fleans.Aspire`
- `message-or-signal-catch.bpmn` deployed

### Steps

1. **Deploy** `message-or-signal-catch.bpmn` via the Web UI or
   `POST https://localhost:7140/Workflow/upload-bpmn`.
2. **Start an instance**:
   `POST https://localhost:7140/Workflow/start` with body
   `{"WorkflowId":"multi-catch-test"}`
3. Confirm `multiCatch` is active in the Web UI.
4. **Test A1 — Message wins**: Send a message:
   `POST https://localhost:7140/Workflow/message` with body
   `{"MessageName":"paymentReceived","CorrelationKey":"order-multi-1","Variables":{"amount":99}}`
5. Refresh the instance view.

### Expected Outcomes (A1 — Message wins)

- [ ] `multiCatch` completes after message delivery.
- [ ] `afterCatch` and `end` complete — workflow reaches **Completed**.
- [ ] Signal subscription for `manualOverride` is cancelled (no orphan).

### Steps (A2 — Signal wins)

1. Start a **new** instance (same workflow).
2. Send a signal instead:
   `POST https://localhost:7140/Workflow/signal` with body
   `{"SignalName":"manualOverride"}`
3. Refresh the instance view.

### Expected Outcomes (A2 — Signal wins)

- [ ] `multiCatch` completes after signal broadcast.
- [ ] Workflow reaches **Completed**.
- [ ] Message subscription for `paymentReceived` is cancelled.

---

## Scenario B — Multiple Intermediate Throw (Two Signals)

A process hits a **multiple intermediate throw event** that fires two signals
(`eventA` and `eventB`) sequentially. Any subscribers waiting on those signals
should be unblocked.

### Prerequisites

- `multi-throw.bpmn` deployed
- Optionally deploy signal catch workflows for `eventA` and/or `eventB` to
  verify delivery

### Steps

1. **Deploy** `multi-throw.bpmn`.
2. (Optional) Deploy and start signal-catch workflows that wait on `eventA`
   and `eventB`.
3. **Start an instance**:
   `POST https://localhost:7140/Workflow/start` with body
   `{"WorkflowId":"multi-throw-test"}`
4. Refresh the instance view.

### Expected Outcomes

- [ ] Thrower workflow reaches **Completed** immediately.
- [ ] Both signals (`eventA` and `eventB`) are broadcast.
- [ ] (If subscribers deployed) subscriber workflows complete after signal
      delivery.

---

## Scenario C — Multiple Boundary Event (Message + Timer)

A user task (`longTask`) has an **interrupting multiple boundary event** with a
message (`cancelOrder`) and a 10-second timer. The user task blocks until it is
completed or interrupted, so the boundary always has a window to fire. If the
message arrives before the timer, the boundary fires via message and cancels the
timer. If the user task is completed before either fires, the workflow follows
the normal path.

### Prerequisites

- `multiple-boundary.bpmn` deployed

### Steps (C1 — Message fires boundary)

1. **Deploy** `multiple-boundary.bpmn`.
2. **Start an instance**:
   `POST https://localhost:7140/Execution/start` with body
   `{"WorkflowId":"multi-boundary-test"}`
3. Within 10 seconds (while `longTask` is active) send a cancel message:
   `POST https://localhost:7140/Execution/message` with body
   `{"MessageName":"cancelOrder","CorrelationKey":"order-boundary-1","Variables":{}}`
4. Refresh the instance view.

### Expected Outcomes (C1 — Message fires)

- [ ] `longTask` is cancelled (interrupted by boundary) — not completed.
- [ ] `escalation` script runs and completes.
- [ ] Workflow reaches **Completed** via `escalationEnd`.
- [ ] Timer subscription is cancelled.

### Steps (C2 — Timer fires boundary)

1. Start a **new** instance.
2. Wait ~10 seconds without sending any message.
3. Refresh the instance view.

### Expected Outcomes (C2 — Timer fires)

- [ ] After ~10s, `longTask` is cancelled (interrupted by timer boundary).
- [ ] `escalation` script runs and completes.
- [ ] Workflow reaches **Completed** via `escalationEnd`.
- [ ] Message subscription for `cancelOrder` is cancelled.
