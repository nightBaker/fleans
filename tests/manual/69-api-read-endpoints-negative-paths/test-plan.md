# 69 — REST read endpoints and API negative paths

> **Automated:** every step below is covered by `src/Fleans/Fleans.E2E.Tests/Specs/`
> `DefinitionsApiTests.cs`, `ExecutionApiTests.cs`, `InstancesApiTests.cs` and
> `CustomTasksApiTests.cs` (#766). Run this plan manually only to spot-check a deployed stack.

Verifies the read-only REST surface that most specs never touch (definition listing, instance
listing by key / version, the custom-task catalog) and that malformed or out-of-state requests
are rejected with a 4xx carrying a useful message — never a 500.

## Prerequisites

- Aspire stack running (`dotnet run --project Fleans.Aspire` from `src/Fleans/`).
- API at `https://localhost:7140` (all `curl` calls below use `-k`).

Fixtures (this folder):

| File | Purpose |
|---|---|
| `api-read-endpoints.bpmn` | key `api-read-endpoints`; `start → wait → end`, where `wait` is an unregistered custom task, so instances park until `complete-activity` |
| `api-disable-start.bpmn` | key `api-disable-start`; `start → end`, disabled/enabled by step 6 |
| `malformed.bpmn` | not well-formed XML |
| `no-process.bpmn` | well-formed BPMN with no `<process>` (collaboration only) |

Deploy a fixture with:

```bash
jq -Rs '{bpmnXml: .}' tests/manual/69-api-read-endpoints-negative-paths/<file> \
  | curl -sk -X POST https://localhost:7140/Definitions/deploy -H 'Content-Type: application/json' -d @- -i
```

## Definitions

### 1. List definitions

Deploy `api-read-endpoints.bpmn` twice (note both `version` values).

```bash
curl -sk 'https://localhost:7140/Definitions?pageSize=100&sorts=-Version&filters=ProcessDefinitionKey==api-read-endpoints'
```

- [ ] 200; every item has `processDefinitionKey = "api-read-endpoints"`; first item is the newest version, `isActive = true`, `activitiesCount = 3`, `sequenceFlowsCount = 2`.
- [ ] `?page=2&pageSize=1&sorts=-Version&filters=…` returns exactly the second-newest version, same `totalCount`.
- [ ] `filters=ProcessDefinitionKey==no-such-key` returns `totalCount = 0` (200, not 404).

### 2. Instances by key and by version

Start an instance (`POST /Execution/start {"workflowId":"api-read-endpoints"}`) → deploy again → start another.

- [ ] `GET /Definitions/api-read-endpoints/instances` lists both; each row has `isStarted = true`, `isCompleted = false`.
- [ ] `GET /Definitions/api-read-endpoints/{v1}/instances` lists only the first; `/{v2}/instances` only the second.
- [ ] After `POST /Execution/complete-activity {"workflowInstanceId":"<first>","activityId":"wait"}`, `…/instances?filters=IsCompleted==true` lists the first and not the second.
- [ ] Unknown key or version (`/Definitions/no-such-key/instances`, `/Definitions/api-read-endpoints/999999/instances`) → 200 with `totalCount = 0`.

### 3. Deploy negative paths

- [ ] `malformed.bpmn` → **400**, body `{"error":"Invalid BPMN: <XML parser diagnostic>"}`; no `api-malformed` definition appears in step 1's list.
- [ ] `no-process.bpmn` → **400**, body contains `Invalid BPMN: BPMN file must contain a process element`.
- [ ] `{"bpmnXml":"this is not xml"}` → **400** `Invalid BPMN: …`; `{"bpmnXml":"   "}` → **400** `BpmnXml is required.`
- [ ] `POST /Definitions/disable` and `/enable` with `{"processDefinitionKey":"no-such-key"}` → **404** (`… is not registered …`).

## Execution

### 4. Start unknown / missing key

- [ ] `POST /Execution/start {"workflowId":"no-such-process"}` → **404**, ProblemDetails `detail` names the key and says `not registered`.
- [ ] `{"workflowId":"  "}` → **400** `WorkflowId is required`.

### 5. Start a disabled process

Deploy `api-disable-start.bpmn`, then `POST /Definitions/disable {"processDefinitionKey":"api-disable-start"}`.

- [ ] `POST /Execution/start {"workflowId":"api-disable-start"}` → **409**, detail `Process 'api-disable-start' is disabled. Enable it before creating new instances.`; no instance is created.
- [ ] After `POST /Definitions/enable …` the same start → 200 and the instance completes.

### 6. complete-activity negative paths

Start an `api-read-endpoints` instance (parks on `wait`).

- [ ] `activityId = "no-such-activity"` → **409** `No active entry found for activity 'no-such-activity'.`; instance still waiting.
- [ ] `activityId = "start"` (exists, already completed) → **409**.
- [ ] `activityId = "wait"` → 200, instance completes; repeating it → **409** and `wait` appears exactly once in `completedActivities`.
- [ ] Random `workflowInstanceId` → **404** `Workflow instance '<id>' not found.` (pre-#766 this was a 409 leaking `ProcessDefinitionId not set — call SetWorkflow first.`); `GET /Instances/<id>/state` stays 404.
- [ ] `workflowInstanceId = 00000000-…` or blank `activityId` → **400**.

## Instances

### 7. State of an unknown instance

- [ ] `GET /Instances/<random-guid>/state` → **404** `{"error":"Instance <id> not found"}`.
- [ ] `GET /Instances/not-a-guid/state` → **404** (route constraint).

## Custom-task catalog

### 8. Catalog

- [ ] `GET /custom-tasks` → 200; contains `taskType = "rest-call"`, `displayName = "REST Caller"`, non-empty `siloNames`, and a `parameterSchema` whose `url` parameter is `required: true`, `type: "String"`, and `headers` is `type: "Map"`, `itemType: "String"`.
- [ ] `GET /custom-tasks/rest-call` → 200, same entry.
- [ ] `GET /custom-tasks/no-such-type` → **404**.

## Pass criteria

All checkboxes pass and no request in this plan returns a 5xx.
