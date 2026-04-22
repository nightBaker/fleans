---
title: REST API
description: HTTP endpoints exposed by Fleans.Api.
---

All endpoints are served from `https://localhost:7140/Workflow/*` by default.

| Endpoint | Method | Body |
|---|---|---|
| `/start` | POST | `{"WorkflowId":"process-id"}` or `{"WorkflowId":"process-id","Variables":{"key":"value"}}` — `Variables` is optional; when provided, the variables are merged into the root scope **before** the workflow starts (required for message event sub-processes that resolve correlation keys from variables at scope entry) |
| `/message` | POST | `{"MessageName":"...", "CorrelationKey":"...", "Variables":{}}` |
| `/signal` | POST | `{"SignalName":"..."}` |
| `/complete-activity` | POST | `{"WorkflowInstanceId":"guid", "ActivityId":"activity-id", "Variables":{}}` |
| `/instances/{instanceId}/state` | GET | *(none)* — Returns the current state snapshot for a specific workflow instance |

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
