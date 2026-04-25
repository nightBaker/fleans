# Instance State Endpoint — Manual Test Plan

## Scenario

Verify `GET /Workflow/instances/{instanceId}/state` returns per-instance state with correct JSON shape and active activity tracking.

## Prerequisites

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`)
- Deploy `tests/load/fixtures/events-workflow.bpmn` via the Workflows UI at `https://localhost:7140/workflows`

## Steps

1. Start an instance with a correlation variable:
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/start \
     -H "Content-Type: application/json" \
     -d '{"WorkflowId":"load-events","Variables":{"requestId":"test-123"}}'
   ```
   Note the `workflowInstanceId` from the response.

2. Poll the instance state endpoint:
   ```bash
   curl -k https://localhost:7140/Workflow/instances/<instanceId>/state
   ```

3. Verify the response shape:
   - [ ] Response is HTTP 200
   - [ ] JSON keys are camelCase: `activeActivityIds`, `completedActivityIds`, `isStarted`, `isCompleted`
   - [ ] `isStarted` is `true`
   - [ ] `activeActivityIds` contains `"waitMessage"` (the message intermediate catch event)

4. Verify 404 for unknown instance:
   ```bash
   curl -k https://localhost:7140/Workflow/instances/00000000-0000-0000-0000-000000000000/state
   ```
   - [ ] Response is HTTP 404
   - [ ] Body contains `{"error":"Instance 00000000-0000-0000-0000-000000000000 not found"}`

5. Send the correlated message to unblock the instance:
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/message \
     -H "Content-Type: application/json" \
     -d '{"MessageName":"loadMessage","CorrelationKey":"test-123","Variables":{}}'
   ```

6. Poll the state again:
   ```bash
   curl -k https://localhost:7140/Workflow/instances/<instanceId>/state
   ```
   - [ ] `isCompleted` is `true`
   - [ ] `activeActivityIds` is empty
   - [ ] `completedActivityIds` contains `"waitMessage"`
