# Manual Test 30 — Cancel Event (Transaction Cancellation)

## Scenario

A process enters a **Transaction Sub-Process** containing a user task. When the user task
is completed, flow reaches a **Cancel End Event** inside the transaction. The engine must:

1. Set the transaction outcome to `Cancelled`
2. Cancel all still-active activities in the transaction scope
3. Fire the **Cancel Boundary Event** attached to the transaction
4. Execute the recovery path (script task → end event)

## Prerequisites

- Aspire host running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`)
- A clean dev DB (delete SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`)
- `cancel-transaction.bpmn` available in this folder

## Steps

1. **Deploy** the BPMN:

   ```
   POST https://localhost:7140/Workflow/deploy
   Body: { "BpmnXml": "<contents of cancel-transaction.bpmn>" }
   ```

   Expected response:
   ```json
   { "ProcessDefinitionKey": "cancelTransactionProcess", "Version": 1 }
   ```

   Confirm `cancelTransactionProcess` appears in the Web UI deployments list.

2. **Start an instance**:

   ```
   POST https://localhost:7140/Workflow/start
   Body: { "WorkflowId": "cancelTransactionProcess" }
   ```

   Note the returned `WorkflowInstanceId`.

3. **Verify active activity**: In the Web UI or via the state endpoint, confirm the instance
   is waiting at `review_task` (the user task inside the transaction).

   ```
   GET https://localhost:7140/Workflow/instances/{WorkflowInstanceId}/state
   ```

   Expected: `activeActivityIds` contains `"review_task"`.

4. **Complete the user task** (triggering cancel flow):

   ```
   POST https://localhost:7140/Workflow/complete-activity
   Body: {
     "WorkflowInstanceId": "<id>",
     "ActivityId": "review_task",
     "Variables": {}
   }
   ```

5. **Verify completion**: Poll the state endpoint until `isCompleted = true`.

   Expected state:
   - `isCompleted`: `true`
   - `completedActivityIds` contains: `review_task`, `cancel_end`, `cancel_boundary`,
     `recovery_task`, `end`
   - `activeActivityIds`: empty

6. **Verify recovery script ran**: The recovery script sets `cancelled = true` and
   `reason = "payment-rejected"`. These variables should be visible in the workflow instance
   variables state.

## Expected Outcomes

- [ ] Workflow reaches `isCompleted = true`
- [ ] `cancel_boundary` appears in completed activities (Cancel Boundary Event fired)
- [ ] `recovery_task` appears in completed activities (recovery path executed)
- [ ] `end` appears in completed activities
- [ ] No activities are stuck in active state after completion
- [ ] Transaction outcome is `Cancelled` (visible in engine logs or via grain state)
