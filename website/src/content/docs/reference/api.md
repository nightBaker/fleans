---
title: REST API
description: HTTP endpoints exposed by Fleans.Api.
---

All endpoints are served from `https://localhost:7140/Workflow/*` by default.

| Endpoint | Method | Body |
|---|---|---|
| `/deploy` | POST | `{"BpmnXml":"<raw BPMN XML string>"}` |
| `/start` | POST | `{"WorkflowId":"process-id"}` or `{"WorkflowId":"process-id","Variables":{"key":"value"}}` — `Variables` is optional; when provided, the variables are merged into the root scope **before** the workflow starts (required for message event sub-processes that resolve correlation keys from variables at scope entry) |
| `/message` | POST | `{"MessageName":"...", "CorrelationKey":"...", "Variables":{}}` |
| `/signal` | POST | `{"SignalName":"..."}` |
| `/complete-activity` | POST | `{"WorkflowInstanceId":"guid", "ActivityId":"activity-id", "Variables":{}}` |
| `/instances/{instanceId}/state` | GET | *(none)* — Returns the current state snapshot for a specific workflow instance |

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
  "Error": "Failed to parse BPMN: ..."
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
  "Error": "WorkflowId is required"
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

- **400** — `{"Error": "MessageName is required"}`
- **404** — `{"Error": "No active subscription found for message 'payment-received' with correlation key 'order-123'"}` — no workflow instance is currently waiting for this message/key combination

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

- **400** — `{"Error": "SignalName is required"}`
- **404** — `{"Error": "No active subscription found for signal 'global-alert'"}` — no workflow instance is currently listening for this signal

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
  "Error": "WorkflowInstanceId is required"
}
```

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

| Policy | Endpoints | Description |
|--------|-----------|-------------|
| `WorkflowMutation` | `POST /start`, `/complete-activity`, `/message`, `/signal`, `/deploy`, `/evaluate-conditions` | Workflow write operations |
| `TaskOperation` | `POST /claim-user-task`, `/unclaim-user-task`, `/complete-user-task` | User task operations |
| `Read` | `GET /definitions` | Read-only queries |
| `Admin` | `POST /upload-bpmn`, `/disable`, `/enable` | Admin operations |
| `Polling` | `GET /instances/{id}/state` | High-frequency state polling |

#### Environment variable overrides

For Docker Compose or container deployments, use `__` (double underscore) notation:

```bash
RateLimiting__WorkflowMutation__PermitLimit=1000
RateLimiting__WorkflowMutation__Window=1
RateLimiting__Polling__PermitLimit=10000
```

> **Important:** Either configure all five policies or leave the `RateLimiting` section absent entirely. A partial configuration will cause HTTP 500 errors on endpoints whose policy is not registered.
