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
