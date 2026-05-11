# 43 — User Task Fail and Cancel Endpoints

## Scenario: Fail a user task

1. Deploy a workflow with a single user task (use `tests/manual/bpmn/user-task-simple.bpmn` or create one).
2. Start the workflow:
   ```
   POST /api/workflow/start   { "workflowId": "user-task-test" }
   ```
3. List pending tasks and note `activityInstanceId`:
   ```
   GET /api/workflow/tasks
   ```
4. Claim the task:
   ```
   POST /api/workflow/tasks/{activityInstanceId}/claim   { "userId": "test-user" }
   ```
5. Fail the task:
   ```
   POST /api/workflow/tasks/{activityInstanceId}/fail
   { "errorCode": "400", "errorMessage": "User rejected the task" }
   ```
   **Verify:** `200 OK`
6. Verify the task no longer appears in pending tasks:
   ```
   GET /api/workflow/tasks/{activityInstanceId}   → 404
   ```
7. Verify the workflow instance is in error state (if no error boundary event is attached):
   ```
   GET /api/workflow/{workflowInstanceId}   → state contains error with code 400
   ```

## Scenario: Cancel a user task

1. Start the workflow (same as above).
2. List pending tasks, note `activityInstanceId`.
3. Claim the task.
4. Cancel the task:
   ```
   POST /api/workflow/tasks/{activityInstanceId}/cancel
   { "reason": "Operator cancelled" }
   ```
   **Verify:** `200 OK`
5. Verify the task is gone from pending tasks.
6. Verify the workflow branch is terminated.

## Scenario: Cancel without body (reason is optional)

```
POST /api/workflow/tasks/{activityInstanceId}/cancel
(empty body)
```
**Verify:** `200 OK`

## Scenario: Idempotency — double fail

1. Fail a task (step 5 above).
2. Call fail again on the same `activityInstanceId`.
**Verify:** `200 OK` (no error, no duplicate events).

## Scenario: Fail a non-existent task

```
POST /api/workflow/tasks/00000000-0000-0000-0000-000000000000/fail
{ "errorMessage": "test" }
```
**Verify:** `404 Not Found`

## Scenario: Fail with missing ErrorMessage

```
POST /api/workflow/tasks/{activityInstanceId}/fail
{ "errorCode": "500" }
```
**Verify:** `400 Bad Request`

## Scenario: Fail with Error Boundary Event

1. Deploy a workflow with a user task that has an Error Boundary Event attached (catches code `"400"`).
2. Start, claim, then fail with code `"400"`.
**Verify:** The workflow continues via the boundary event path, not top-level failure.

## Universal prerequisite

Aspire stack running: `dotnet run --project src/Fleans/Fleans.Aspire`
