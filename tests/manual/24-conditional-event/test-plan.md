# 24 - Conditional Event

## Scenario

Tests BPMN Conditional Events: a workflow with a **conditional intermediate catch event** that waits until a condition evaluates to true, and a **conditional boundary event** (interrupting) on a script task that fires when a condition becomes true during task execution.

## Prerequisites

- Aspire stack running (`dotnet run --project Fleans.Aspire`)
- Web UI accessible at `https://localhost:7168`

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

## Expected Outcomes

- [ ] Conditional intermediate catch event blocks until condition is true
- [ ] Workflow resumes after condition becomes true
- [ ] Conditional start event creates instance when condition evaluates true
- [ ] Conditional start event does not create instance when condition evaluates false
- [ ] Conditional boundary event (interrupting) cancels host activity when condition fires
- [ ] Variables are correctly available after conditional event completes
