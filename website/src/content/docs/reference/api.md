---
title: REST API
description: HTTP endpoints exposed by Fleans.Api.
sidebar:
  order: 1
---

Workflow endpoints are served from `https://localhost:7140/Workflow/*`; user-task endpoints are served from `https://localhost:7140/UserTasks/*` by default (see [PR #614](https://github.com/nightBaker/fleans/pull/614) for the controller split).

| Endpoint | Method | Body |
|---|---|---|
| `/deploy` | POST | `{"BpmnXml":"<raw BPMN XML string>"}` |
| `/start` | POST | `{"WorkflowId":"process-id"}` or `{"WorkflowId":"process-id","Variables":{"key":"value"}}` — `Variables` is optional; when provided, the variables are merged into the root scope **before** the workflow starts (required for message event sub-processes that resolve correlation keys from variables at scope entry) |
| `/message` | POST | `{"MessageName":"...", "CorrelationKey":"...", "Variables":{}}` |
| `/signal` | POST | `{"SignalName":"..."}` |
| `/complete-activity` | POST | `{"WorkflowInstanceId":"guid", "ActivityId":"activity-id", "Variables":{}}` |
| `/evaluate-conditions` | POST | `{"WorkflowId":"process-id", "Variables":{"key":"value"}}` — Evaluates all conditional start events (or only those for the given `WorkflowId` if provided) against the supplied variables. Returns `{"StartedInstanceIds":["guid",...], "Errors":["..."]}`. `Errors` is present only when one or more listeners failed during evaluation. |
| `/instances/{instanceId}/state` | GET | *(none)* — Returns the current state snapshot for a specific workflow instance |
| `/UserTasks` | GET | *(query string)* — Paginated list of pending user tasks. See [User Task endpoints](#user-task-endpoints). |
| `/UserTasks/{activityInstanceId}` | GET | *(none)* — Single user-task lookup. |
| `/UserTasks/{activityInstanceId}/claim` | POST | `{"UserId":"alice"}` |
| `/UserTasks/{activityInstanceId}/unclaim` | POST | *(empty body)* |
| `/UserTasks/{activityInstanceId}/complete` | POST | `{"UserId":"alice", "Variables":{"approved":true}}` |
| `/UserTasks/{activityInstanceId}/fail` | POST | `{"errorMessage":"reason", "errorCode":"400"}` — fails the task; routes via Error Boundary Event if one matches. |
| `/UserTasks/{activityInstanceId}/cancel` | POST | `{"reason":"optional"}` — cancels the task; no error propagation. Idempotent. |

## Endpoint details

### `POST /Workflow/deploy`

Deploys a BPMN process definition to the engine. The request body contains the raw BPMN XML as a string. On success the engine parses the XML, registers the process definition, and returns the assigned key and version number. If the same process ID is deployed again, the version is incremented automatically.

**Why this endpoint exists:** Before any workflow instance can be started, its process definition must be deployed. This endpoint is the programmatic entry point for uploading BPMN definitions — the Web UI also uses it internally when you import a `.bpmn` file.

**Request**

```json
POST /Workflow/deploy
Content-Type: application/json

{
  "BpmnXml": "<?xml version=\"1.0\" encoding=\"UTF-8\"?><definitions ...>...</definitions>"
}
```

**Success response (200)**

```json
{
  "ProcessDefinitionKey": "my-process",
  "Version": 1
}
```

**Error response (400)** — returned when the BPMN XML cannot be parsed:

```json
{
  "error": "Failed to parse BPMN: ..."
}
```

**Best-practice example**

```bash
# Deploy a local BPMN file via curl
BPMN_XML=$(cat my-workflow.bpmn | jq -Rs .)
curl -s -X POST https://localhost:7140/Workflow/deploy \
  -H "Content-Type: application/json" \
  -d "{\"BpmnXml\": $BPMN_XML}"
```

This reads the `.bpmn` file, JSON-escapes it with `jq -Rs`, and sends it to the deploy endpoint. The response contains the `ProcessDefinitionKey` you pass to `/start` to create instances.

### `POST /Workflow/start`

Starts a new workflow instance from a deployed process definition. Returns the instance ID which can be used to track state, send messages, or complete activities.

**Request**

```json
POST /Workflow/start
Content-Type: application/json

{
  "WorkflowId": "my-process",
  "Variables": { "amount": 100 }
}
```

`Variables` is optional. When provided, variables are merged into the root scope before the workflow starts.

**Success response (200)**

```json
{
  "WorkflowInstanceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

**Error response (400)**

```json
{
  "error": "WorkflowId is required"
}
```

### `POST /Workflow/message`

Delivers a message to workflow instances waiting for it, correlated by key. Used to trigger intermediate message catch events and message start events.

**Request**

```json
POST /Workflow/message
Content-Type: application/json

{
  "MessageName": "payment-received",
  "CorrelationKey": "order-123",
  "Variables": { "paymentId": "pay-456" }
}
```

**Success response (200)**

```json
{
  "Delivered": true,
  "WorkflowInstanceIds": ["3fa85f64-5717-4562-b3fc-2c963f66afa6"]
}
```

**Error responses**

- **400** — `{"error": "MessageName is required"}`
- **404** — `{"error": "No active subscription found for message 'payment-received' with correlation key 'order-123'"}` — no workflow instance is currently waiting for this message/key combination

### `POST /Workflow/signal`

Broadcasts a signal to all workflow instances listening for it. Unlike messages, signals have no correlation key — every matching listener receives the signal.

**Request**

```json
POST /Workflow/signal
Content-Type: application/json

{
  "SignalName": "global-alert"
}
```

**Success response (200)**

```json
{
  "DeliveredCount": 2,
  "WorkflowInstanceIds": [
    "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8"
  ]
}
```

**Error responses**

- **400** — `{"error": "SignalName is required"}`
- **404** — `{"error": "No active subscription found for signal 'global-alert'"}` — no workflow instance is currently listening for this signal

### `POST /Workflow/complete-activity`

Completes a manual activity (e.g., a task waiting for external input) on a running workflow instance, optionally passing output variables.

**Request**

```json
POST /Workflow/complete-activity
Content-Type: application/json

{
  "WorkflowInstanceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "ActivityId": "review-task",
  "Variables": { "approved": true }
}
```

**Success response (200)** — empty body

**Error response (400)**

```json
{
  "error": "WorkflowInstanceId is required"
}
```

### User Task endpoints

<!-- DRIFT-GUARD: route + body shapes verified against the [UserTasksController](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Api/Controllers/UserTasksController.cs) (controller split landed in PR #614 / issue #587). Re-verify when controller changes. -->

User-task endpoints expose the human-in-the-loop lifecycle of `<bpmn:userTask>` activities. The conceptual model (states, who-can-claim, expected outputs) lives in the [User Tasks guide](/fleans/guides/user-tasks/) — this section is the authoritative wire reference.

**User Task operations summary**

| Verb | Path | Surface | Auth | Success |
| --- | --- | --- | --- | --- |
| GET  | `/UserTasks`                            | Query    | optional | `200 OK` |
| GET  | `/UserTasks/{activityInstanceId}`       | Query    | optional | `200 OK` |
| POST | `/UserTasks/{activityInstanceId}/claim`    | Mutation | `UserId` required in body | `200 OK` |
| POST | `/UserTasks/{activityInstanceId}/unclaim`  | Mutation | NONE — see below | `200 OK` |
| POST | `/UserTasks/{activityInstanceId}/complete` | Mutation | `UserId` required in body | `200 OK` |
| POST | `/UserTasks/{activityInstanceId}/fail`     | Mutation | `ErrorMessage` required in body | `200 OK` |
| POST | `/UserTasks/{activityInstanceId}/cancel`   | Mutation | body optional | `200 OK` |

`Auth` above refers to API-level JWT bearer auth, which is opt-in for the entire API — see [Authentication](/fleans/reference/authentication/#quick-start). The `UserId` field in claim/complete bodies is **caller identity**, not authentication: the engine treats whatever value it receives as the acting user.

#### Error response shapes

The User Task endpoints emit two distinct error wire shapes depending on which layer rejects the request:

| Status | Surface | Wire shape |
| --- | --- | --- |
| `400` (model-binding) | ASP.NET auto via `[ApiController]` + `AddProblemDetails()` | `application/problem+json` (RFC 7807) |
| `400` (controller-emitted, e.g. `UserId is required`) | Custom `ErrorResponse` | `{"error":"..."}` |
| `404` (task not found) | Custom `ErrorResponse` | `{"error":"..."}` |
| `409` (wrong claimer / missing required outputs) | Custom `ErrorResponse` | `{"error":"..."}` (the two cases discriminate only by message text — see the `/complete` section below) |
| `5xx` (unhandled exception) | `GlobalExceptionHandler` ProblemDetails | `application/problem+json` (RFC 7807) |

Property casing in the controller-emitted `{"error":"..."}` shape is camelCase — the Fleans API serializes via the System.Text.Json default policy.

#### `GET /UserTasks`

Lists pending user tasks across all running workflow instances, with optional filters and standard pagination/sort/filter query parameters.

**Request**

```
GET /UserTasks?assignee=alice&candidateGroup=approvers&page=1&pageSize=20&sorts=&filters=
```

| Query param | Type | Description |
|---|---|---|
| `assignee` | `string?` | Restrict to tasks assigned to this user (matches `Assignee` field). |
| `candidateGroup` | `string?` | Restrict to tasks where this group is a candidate. |
| `page` | `int` (default `1`) | 1-based page index. |
| `pageSize` | `int` (default `20`) | Page size. |
| `sorts` | `string?` | Sieve-style sort expression (e.g. `createdAt`, `-createdAt`). |
| `filters` | `string?` | Sieve-style filter expression. |

**Success response (200)** — paginated envelope with a `data: UserTaskResponse[]` payload:

```json
{
  "data": [
    {
      "workflowInstanceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "activityInstanceId": "8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8",
      "activityId": "review-task",
      "assignee": "alice",
      "candidateGroups": ["approvers"],
      "candidateUsers": [],
      "claimedBy": null,
      "taskState": "Created",
      "createdAt": "2026-05-03T10:30:00+00:00",
      "expectedOutputVariables": ["approved"]
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

**Curl example**

```bash
curl -k "https://localhost:7140/UserTasks?assignee=alice&page=1&pageSize=20"
```

#### `GET /UserTasks/{activityInstanceId}`

Returns a single user task by its activity-instance id, or `404` if the id is unknown / no longer pending.

**Request**

```
GET /UserTasks/8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8
```

**Success response (200)** — single `UserTaskResponse` (same shape as the array element above).

**Error response (404)**

```json
{ "error": "User task '8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8' not found" }
```

**Curl example**

```bash
curl -k https://localhost:7140/UserTasks/8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8
```

#### `POST /UserTasks/{activityInstanceId}/claim`

Claims a pending task for `UserId`. Subsequent claims by a different user overwrite the claim — Fleans does not enforce first-claim-wins (see the [User Tasks guide](/fleans/guides/user-tasks/) for the lifecycle table).

**Request**

```json
POST /UserTasks/8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8/claim
Content-Type: application/json

{ "UserId": "alice" }
```

| Body field | Type | Required | Description |
|---|---|---|---|
| `UserId` | `string` | yes | Caller identity attached to the claim. |

**Success response (200)** — empty body.

**Error responses**

- **400** — `{"error": "UserId is required"}` — body missing or `UserId` empty/whitespace.
- **404** — `{"error": "User task '<id>' not found"}` — no pending task with that activity-instance id.
- **409** — claim rejected by the domain layer (e.g. caller is not in `Assignee` / `CandidateUsers` / `CandidateGroups`); the body is the underlying `InvalidOperationException` message.

**Curl example**

```bash
curl -k -X POST https://localhost:7140/UserTasks/8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8/claim \
  -H "Content-Type: application/json" \
  -d '{"UserId":"alice"}'
```

#### `POST /UserTasks/{activityInstanceId}/unclaim`

Releases an existing claim so another user can claim the task. The body is empty.

:::caution[No authorization on unclaim]
Unlike `/claim`, `/unclaim` does **not** verify that the caller is the current claimer — any caller with API access can unclaim a task that was claimed by another user. Treat the API as administrative and gate it via API-level auth — see [Authentication](/fleans/reference/authentication/#quick-start).
:::

**Request**

```
POST /UserTasks/8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8/unclaim
Content-Type: application/json
```

**Success response (200)** — empty body. Note: succeeds whether the task was previously claimed or not.

**Error response (404)**

```json
{ "error": "User task '8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8' not found" }
```

**Curl example**

```bash
curl -k -X POST https://localhost:7140/UserTasks/8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8/unclaim \
  -H "Content-Type: application/json"
```

#### `POST /UserTasks/{activityInstanceId}/complete`

Completes the task on behalf of `UserId`, merging `Variables` into the enclosing scope and advancing the token. The caller **must** be the current claimer, and **all** variables declared in `<fleans:expectedOutputs>` must be present.

**Request**

```json
POST /UserTasks/8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8/complete
Content-Type: application/json

{
  "UserId": "alice",
  "Variables": { "approved": true, "reviewerComment": "looks good" }
}
```

| Body field | Type | Required | Description |
|---|---|---|---|
| `UserId` | `string` | yes | Caller identity — must match `claimedBy` on the task. |
| `Variables` | `object?` | optional unless the task declares `expectedOutputVariables` | Output variables merged into the workflow's enclosing scope. Every entry in `expectedOutputVariables` must have a value here. |

**Success response (200)** — empty body. The task is removed from the registry; subsequent `GET /UserTasks/{id}` returns `404`.

**Error responses**

- **400** — `{"error": "UserId is required"}`
- **404** — `{"error": "User task '<id>' not found"}` — already completed, never existed, or wrong id.
- **409 — wrong claimer**:

  ```json
  { "error": "Task is claimed by bob, not alice" }
  ```

  Distinguishable by the `Task is claimed by …, not …` prefix.

- **409 — missing required outputs**:

  ```json
  { "error": "Missing required output variables: approved, reviewerComment" }
  ```

  Distinguishable by the `Missing required output variables:` prefix. The list is the comma-joined set of declared `<fleans:expectedOutputs>` entries that are absent from the supplied `Variables`.

**Curl example**

```bash
curl -k -X POST https://localhost:7140/UserTasks/8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8/complete \
  -H "Content-Type: application/json" \
  -d '{"UserId":"alice","Variables":{"approved":true}}'
```

#### `POST /UserTasks/{activityInstanceId}/fail`

Fails a pending user task with an error code and message. The engine routes the failure through the standard `FailActivity` path — if an Error Boundary Event is attached to the task and its error code matches, the workflow continues via that boundary; otherwise the workflow instance enters a top-level error state.

Both operations are **idempotent**: calling them on a task that is already in a terminal state (`Completed`, `Failed`, or `Cancelled`) returns `200 OK` without re-emitting events.

**Request body**

```json
{
  "errorMessage": "User rejected the task",
  "errorCode": "400"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `errorMessage` | `string` | **Yes** | Human-readable failure reason. |
| `errorCode` | `string` | No (default `"500"`) | Error code matched against Error Boundary Events. |

**Response codes**

- **200** — task failed (or was already terminal — idempotent).
- **400** — `{"error": "ErrorMessage is required"}`.
- **404** — `{"error": "User task '<id>' not found"}` — task never existed.

**Curl example**

```bash
curl -k -X POST https://localhost:7140/UserTasks/8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8/fail \
  -H "Content-Type: application/json" \
  -d '{"errorMessage":"User rejected the task","errorCode":"400"}'
```

#### `POST /UserTasks/{activityInstanceId}/cancel`

Cancels a pending user task. The activity is marked terminal with `ActivityCancelled` and removed from the task list. No error propagation occurs — the workflow branch simply stops at the cancelled task. The request body is optional.

**Request body (optional)**

```json
{ "reason": "Operator cancelled" }
```

| Field | Type | Required | Description |
|---|---|---|---|
| `reason` | `string?` | No | Free-text cancellation reason stored in logs. |

**Response codes**

- **200** — task cancelled (or was already terminal — idempotent).
- **404** — `{"error": "User task '<id>' not found"}` — task never existed.

**Curl example**

```bash
curl -k -X POST https://localhost:7140/UserTasks/8b2e1a7c-9d3f-4e5b-a1c2-d3e4f5a6b7c8/cancel \
  -H "Content-Type: application/json" \
  -d '{"reason":"Operator cancelled"}'
```

#### See also

- [User Tasks guide](/fleans/guides/user-tasks/) — conceptual model, state diagram, BPMN authoring (`<fleans:expectedOutputs>`).
- [Authentication](/fleans/reference/authentication/) — opt-in JWT bearer auth that gates every `/Workflow/*` endpoint, including the User Task surface.

### Instance State endpoint

`GET /Workflow/instances/{instanceId}/state` returns a per-instance state snapshot including `activeActivityIds`, `completedActivityIds`, `isStarted`, `isCompleted`, and related fields.

This endpoint is intended for **diagnostics and load-test polling**, not for high-frequency production use. The response reflects the read-side EF projection, which is eventually consistent with the event stream — callers that need realtime certainty should drive via the grain API directly.

```bash
curl -k https://localhost:7140/Workflow/instances/<guid>/state
```

> `-k` (or `--insecure`) skips dev-cert validation. In production behind a proper TLS cert, drop the flag.

Returns 404 with `{"error":"Instance {id} not found"}` if the instance ID does not exist in the projection.

Rate limiting: uses the `polling` policy. See [Rate Limiting](#rate-limiting) below for opt-in semantics.

### Rate limiting

All API endpoints have rate-limiting attributes (`workflow-mutation`, `task-operation`, `read`, `admin`, `polling`), but rate limiting is **opt-in**: it only activates when the `RateLimiting` configuration section is present. Default `appsettings.json` has no such section, so the middleware is off by default. When activating rate limiting, populate **all five** policies together — partially populating the section causes unregistered-policy endpoints to return HTTP 500.

#### Configuration example

Add this to your `appsettings.json` (or `appsettings.Production.json`):

```json
{
  "RateLimiting": {
    "WorkflowMutation": { "Window": 60, "PermitLimit": 100 },
    "TaskOperation":    { "Window": 60, "PermitLimit": 100 },
    "Read":             { "Window": 60, "PermitLimit": 200 },
    "Admin":            { "Window": 60, "PermitLimit": 50  },
    "Polling":          { "Window": 1,  "PermitLimit": 1000 }
  }
}
```

- **`Window`** — time window in seconds (default: 60)
- **`PermitLimit`** — maximum requests per window per client IP (default: 100)

The rate limiter uses a **fixed window** algorithm, partitioned by the client's `RemoteIpAddress`.

#### Policy → endpoint mapping

{/* drift-guard: WorkflowController.cs:32-285 — verify each method's [EnableRateLimiting(...)] + [HttpGet|HttpPost(...)] route attribute matches one row. Pinned at branch=docs/401-rate-limit-table-audit SHA=b7d80af; refresh if any method's policy attribute is renamed. */}

All paths below are relative to the `/Workflow` controller route.

| Policy | Endpoints | Description |
|--------|-----------|-------------|
| `WorkflowMutation` | `POST /start`, `/message`, `/signal`, `/evaluate-conditions`, `/deploy` | Workflow lifecycle write operations (start instance, deliver event, evaluate conditions, deploy BPMN) |
| `TaskOperation` | `POST /complete-activity`, `/tasks/{activityInstanceId}/claim`, `/tasks/{activityInstanceId}/unclaim`, `/tasks/{activityInstanceId}/complete`, `/tasks/{activityInstanceId}/fail`, `/tasks/{activityInstanceId}/cancel` | Activity-completion + user-task operations — see [User Tasks guide](/fleans/guides/user-tasks/) |
| `Read` | `GET /definitions`, `/definitions/{key}/instances`, `/definitions/{key}/{version}/instances`, `/tasks`, `/tasks/{activityInstanceId}` | Read-only queries |
| `Admin` | `POST /disable`, `/enable` | Admin operations on process definitions |
| `Polling` | `GET /instances/{instanceId}/state` | High-frequency state polling |

#### Environment variable overrides

For Docker Compose or container deployments, use `__` (double underscore) notation:

```bash
RateLimiting__WorkflowMutation__PermitLimit=1000
RateLimiting__WorkflowMutation__Window=1
RateLimiting__Polling__PermitLimit=10000
```

> **Important:** Either configure all five policies or leave the `RateLimiting` section absent entirely. A partial configuration will cause HTTP 500 errors on endpoints whose policy is not registered.

### Authentication

The API supports opt-in JWT bearer authentication, **disabled by default**. See [Authentication](/fleans/reference/authentication/) for configuration, provider walkthroughs, and client examples.
