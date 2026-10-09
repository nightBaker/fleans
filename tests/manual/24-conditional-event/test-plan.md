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
3. Parallel Gateway `fork` — splits into two branches:
   - Conditional Intermediate Catch Event `wait-for-amount` — waits for `amount > 500`, then Script Task `after-condition` (sets `result = "condition-met"`) → End Event `end`
   - Task `update-amount` (external, completed via API) → End Event `end-update`

Conditional watchers are re-evaluated whenever **another activity in the same instance completes**. There is no "patch variables" API, so the fixture provides the `update-amount` task on a parallel branch: completing it with variables merges them into the instance scope and triggers the watcher evaluation. (Completing an already-completed script task such as `set-initial` returns **409 Conflict** — that is not a way to inject variables.)

Conditions use the same syntax as sequence-flow conditions: bare variable names (`amount > 500`) or `${...}` placeholders are rewritten to `_context.<name>` at deploy time.

`conditional-start-event.bpmn` — Process `conditional-start-test`: Conditional Start Event `condStart` (`temperature > 100`) → Script Task `process-alert` (sets `alert = "temperature-exceeded"`) → End Event.

## Steps

### Test A: Conditional Intermediate Catch Event

1. **Deploy** the BPMN fixture via Web UI (upload `conditional-event-test.bpmn`)
2. **Start** a new workflow instance for `conditional-event-test`
3. **Verify** the workflow pauses with `wait-for-amount` and `update-amount` both active
4. **Complete** `update-amount` with a value that does NOT satisfy the condition:
   ```
   POST https://localhost:7140/Execution/complete-activity
   {"WorkflowInstanceId": "<id>", "ActivityId": "update-amount", "Variables": {"amount": 100}}
   ```
5. **Verify** `wait-for-amount` is still active and the instance is not completed
6. **Start** a second instance, wait for both activities to be active, then complete `update-amount` with `{"amount": 600}`
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

- [ ] Conditional intermediate catch event blocks until condition is true
- [ ] Workflow resumes after condition becomes true
- [ ] Conditional start event creates instance when condition evaluates true
- [ ] Conditional start event does not create instance when condition evaluates false
- [ ] Conditional boundary event (interrupting) cancels host activity when condition fires
- [ ] Variables are correctly available after conditional event completes
