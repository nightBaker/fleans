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
| `/evaluate-conditions` | POST | `{"WorkflowId":"process-id", "Variables":{"key":"value"}}` — Evaluates all conditional start events (or only those for the given `WorkflowId` if provided) against the supplied variables. Returns `{"StartedInstanceIds":["guid",...], "Errors":["..."]}`. `Errors` is present only when one or more listeners failed during evaluation. |
