# 24 - Conditional Event

## Scenario

Tests BPMN Conditional Events: a workflow with a **conditional intermediate catch event** that waits until a condition evaluates to true, and a **conditional boundary event** (interrupting) on a script task that fires when a condition becomes true during task execution.

## Prerequisites

- Aspire stack running (`dotnet run --project Fleans.Aspire`)
- Web UI accessible at `https://localhost:7124`

## Fixture

`conditional-event-test.bpmn` — Process `conditional-event-test`:

1. Start Event
2. Script Task `set-initial` — sets `amount = 0`
3. Conditional Intermediate Catch Event `wait-for-amount` — waits for `amount > 500`
4. Script Task `after-condition` — sets `result = "condition-met"`
5. End Event

The conditional intermediate catch event will be evaluated each time the execution loop runs after variable changes. The test requires an external `complete-activity` call with variables to trigger the condition.

## Steps

### Test A: Conditional Intermediate Catch Event

1. **Deploy** the BPMN fixture via Web UI (upload `conditional-event-test.bpmn`)
2. **Start** a new workflow instance for `conditional-event-test`
3. **Verify** the workflow pauses at `wait-for-amount` (check active activities in Web UI)
4. **Complete** the `set-initial` script task with variables `{"amount": 600}`:
   ```
   POST https://localhost:7140/Workflow/complete-activity
   {"WorkflowInstanceId": "<id>", "ActivityId": "set-initial", "Variables": {"amount": 600}}
   ```
5. **Verify** the conditional catch event fires and the workflow completes with `result = "condition-met"`

### Test B: Conditional Start Event (via API)

1. Deploy a process with a `ConditionalStartEvent`
2. Call the evaluate-conditions endpoint:
   ```
   POST https://localhost:7140/Workflow/evaluate-conditions
   {"Variables": {"temperature": 150}}
   ```
3. Verify a new workflow instance is created (condition `temperature > 100` evaluates true)
4. Call again with `{"Variables": {"temperature": 50}}`
5. Verify no new instance is created (condition evaluates false)

### Test C: Conditional Boundary — condition stays false (`conditional-boundary-on-task.bpmn`)

`hostTask` (plain task) carries an interrupting conditional boundary `_context.decision == "escalate"` and a non-interrupting message boundary `updateBoundary` (message `conditionalBoundaryUpdate`, correlation key `orderId`) used to inject variables. Automated by `ConditionalBoundaryEventTests.InterruptingConditionalBoundary_ConditionStaysFalse_HostCompletesNormally`.

1. Deploy `conditional-boundary-on-task.bpmn`; start `conditional-boundary-on-task` with `{"orderId": "cb-1"}`
2. `POST https://localhost:7140/Execution/message` `{"MessageName":"conditionalBoundaryUpdate","CorrelationKey":"cb-1","Variables":{"decision":"approve"}}`
3. Verify `endUpdate` completes and `hostTask` is still active (boundary did not fire)
4. `POST /Execution/complete-activity` for `hostTask`
5. Verify the instance completes via `endNormal`; `condBoundary` / `escalated` not completed

### Test D: Conditional Boundary — condition becomes true

> **KNOWN BUG:** the conditional watcher only reads the host's own variable scope, and both the message-boundary payload and parallel-branch writes land in cloned scopes, so the boundary never fires. See [#784](https://github.com/nightBaker/fleans/issues/784). Specs `InterruptingConditionalBoundary_ConditionBecomesTrue_…` and `…_VariableChangedInParallelBranch_Fires` are `[Ignore]`d.

1. Repeat Test C with `"decision":"escalate"` → expect `hostTask` cancelled, `escalated` + `endBoundary` completed, `handled = "boundary-fired"`
2. Deploy `conditional-boundary-parallel-branch.bpmn`, start it, `complete-activity` `triggerTask` with `{"decision":"escalate"}` → same expectation

## Expected Outcomes

- [ ] Conditional intermediate catch event blocks until condition is true
- [ ] Workflow resumes after condition becomes true
- [ ] Conditional start event creates instance when condition evaluates true
- [ ] Conditional start event does not create instance when condition evaluates false
- [ ] Conditional boundary event (interrupting) cancels host activity when condition fires
- [ ] Variables are correctly available after conditional event completes
