# 45 — User Task Fail and Cancel Endpoints

> **Automated:** every scenario below is covered by
> `src/Fleans/Fleans.E2E.Tests/Specs/UserTaskLifecycleTests.cs` (`Fail_*`, `Cancel_*`).
> Run this plan manually only to spot-check against a deployed stack.

## Scenario: Fail a user task

1. Deploy `tests/manual/45-user-task-fail-cancel/user-task-simple.bpmn` (process key `user-task-test`; user task `review-task` assigned to `test-user`, Error Boundary catching code `"400"`).
2. Start the workflow:
   ```
   POST /Execution/start   { "workflowId": "user-task-test" }
   ```
3. List pending tasks and note `activityInstanceId`:
   ```
   GET /UserTasks?assignee=test-user
   ```
4. Claim the task:
   ```
   POST /UserTasks/{activityInstanceId}/claim   { "userId": "test-user" }
   ```
5. Fail the task:
   ```
   POST /UserTasks/{activityInstanceId}/fail
   { "errorCode": "500", "errorMessage": "User rejected the task" }
   ```
   **Verify:** `200 OK`
6. Verify the task no longer appears in pending tasks:
   ```
   GET /UserTasks/{activityInstanceId}   → 404
   ```
7. Verify the user task failed. Use an error code other than `"400"` (e.g. `"500"`) to bypass the fixture's Error Boundary:
   ```
   GET /Instances/{workflowInstanceId}/state   → `review-task` in completedActivities with errorState.code = the code you sent
   ```

## Scenario: Cancel a user task

1. Start the workflow (same as above).
2. List pending tasks, note `activityInstanceId`.
3. Claim the task.
4. Cancel the task:
   ```
   POST /UserTasks/{activityInstanceId}/cancel
   { "reason": "Operator cancelled" }
   ```
   **Verify:** `200 OK`
5. Verify the task is gone from pending tasks.
6. Verify the workflow branch is terminated.

## Scenario: Cancel without body (reason is optional)

```
POST /UserTasks/{activityInstanceId}/cancel
(empty body)
```
**Verify:** `200 OK`

## Scenario: Idempotency — double fail / cancel on a terminal task (#536)

1. Fail (or cancel) a task (step 5 above).
2. Call fail again, then cancel, on the same `activityInstanceId`.
**Verify:** `200 OK` for both (no error, no duplicate events — the activity's error state / cancellation reason is unchanged).

## Scenario: Fail a non-existent task

```
POST /UserTasks/00000000-0000-0000-0000-000000000000/fail
{ "errorMessage": "test" }
```
**Verify:** `404 Not Found`

## Scenario: Fail with missing ErrorMessage

```
POST /UserTasks/{activityInstanceId}/fail
{ "errorCode": "500" }
```
**Verify:** `400 Bad Request`

## Scenario: Fail with Error Boundary Event

1. Deploy a workflow with a user task that has an Error Boundary Event attached (catches code `"400"`).
2. Start, claim, then fail with code `"400"`.
**Verify:** The workflow continues via the boundary event path, not top-level failure.

## Universal prerequisite

Aspire stack running: `dotnet run --project src/Fleans/Fleans.Aspire`
