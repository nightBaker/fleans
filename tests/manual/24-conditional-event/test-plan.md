# 24 - Conditional Event

## Scenario

Tests BPMN Conditional Events: a workflow with a **conditional intermediate catch event** that waits until a condition evaluates to true, and a **conditional boundary event** (interrupting) on a script task that fires when a condition becomes true during task execution.

## Prerequisites

- Aspire stack running (`dotnet run --project Fleans.Aspire`)
- Web UI accessible at `https://localhost:7124`

## Fixture

`conditional-event-test.bpmn` — Process `conditional-event-test` (`amount` is a start variable):

1. Start Event
2. Parallel Gateway `fork` — splits into two branches:
   - Conditional Intermediate Catch Event `wait-for-amount` — waits for `amount > 500`, then Script Task `after-condition` (sets `result = "condition-met"`) → End Event `end`
   - Task `parallel-task` (external, completed via API) → End Event `end-parallel`

How the watcher fires: conditional watchers are re-evaluated whenever **another activity in the same instance completes**, against the catch event's **own variable scope**. Each parallel branch gets a *cloned copy* of the variables at the fork, so variables passed when completing `parallel-task` are NOT visible to `wait-for-amount` — the completion is only the evaluation trigger. The value the condition sees is the `amount` start variable inherited at the fork. There is currently no API to change the variables of a waiting branch; completing an already-completed activity returns **409 Conflict**.

Conditions use the same syntax as sequence-flow conditions: bare variable names (`amount > 500`) or `${...}` placeholders are rewritten to `_context.<name>` at deploy time.

`conditional-start-event.bpmn` — Process `conditional-start-test`: Conditional Start Event `condStart` (`temperature > 100`) → Script Task `process-alert` (sets `alert = "temperature-exceeded"`) → End Event.

## Steps

### Test A: Conditional Intermediate Catch Event

1. **Deploy** the BPMN fixture via Web UI (upload `conditional-event-test.bpmn`)
2. **Start** an instance with a value that does NOT satisfy the condition:
   ```
   POST https://localhost:7140/Execution/start
   {"WorkflowId": "conditional-event-test", "Variables": {"amount": 100}}
   ```
3. **Verify** the workflow pauses with `wait-for-amount` and `parallel-task` both active
4. **Complete** `parallel-task` to trigger watcher evaluation:
   ```
   POST https://localhost:7140/Execution/complete-activity
   {"WorkflowInstanceId": "<id>", "ActivityId": "parallel-task", "Variables": {}}
   ```
5. **Verify** `wait-for-amount` is still active, `after-condition` did not run, and the instance is not completed
6. **Start** a second instance with `{"amount": 600}`, wait for both activities to be active, then complete `parallel-task`
7. **Verify** the conditional catch event fires and the workflow completes with `result = "condition-met"`

### Test B: Conditional Start Event (via API)

1. Deploy `conditional-start-event.bpmn`
2. Call the evaluate-conditions endpoint:
   ```
   POST https://localhost:7140/Execution/evaluate-conditions
   {"WorkflowId": "conditional-start-test", "Variables": {"temperature": 150}}
   ```
3. Verify the response has exactly one `StartedInstanceIds` entry and no `Errors`; the new instance completes with `alert = "temperature-exceeded"` (condition `temperature > 100` evaluates true)
4. Call again with `{"WorkflowId": "conditional-start-test", "Variables": {"temperature": 50}}`
5. Verify no new instance is created (condition evaluates false)

## Expected Outcomes

- [ ] Conditional intermediate catch event blocks while condition is false (no evaluation error)
- [ ] Workflow resumes after condition becomes true
- [ ] Conditional start event creates instance when condition evaluates true (response has no `Errors`)
- [ ] Conditional start event does not create instance when condition evaluates false
- [ ] Conditional boundary event (interrupting) cancels host activity when condition fires
- [ ] Variables are correctly available after conditional event completes
