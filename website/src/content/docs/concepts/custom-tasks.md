---
title: Custom Tasks
description: Plug your own service tasks into Fleans by writing a worker-side handler that subscribes to ExecuteCustomTaskEvent and self-filters by task type.
---

A **custom task** is a `<bpmn:serviceTask type="...">` whose execution is supplied by a plugin you write. The plugin lives on a Worker silo as a grain that derives from `CustomTaskHandlerBase`; the engine does not need to know what your plugin does — only that *some* worker can claim the `type`.

This is the recommended pattern for anything Fleans doesn't ship in the box: REST calls, email, Slack, custom database queries, file uploads, etc.

## How it works

1. **BPMN parsing.** The converter sees `<serviceTask type="rest-call">` (or the Camunda equivalent `<extensionElements><zeebe:taskDefinition type="rest-call"/></extensionElements>`) and produces a `CustomTaskActivity` carrying the task type and any `<zeebe:ioMapping>` declarations.
2. **Activity emits an event.** When the workflow reaches the activity, it publishes `ExecuteCustomTaskEvent { TaskType, InputMappings, OutputMappings, VariablesId, … }` on the shared `events/ExecuteCustomTaskEvent` Orleans stream.
3. **Plugin handlers fan out and self-filter.** Every plugin's `CustomTaskHandlerBase` subclass on every Worker silo receives the event via Orleans implicit-stream subscription. Each handler checks `if (item.TaskType != MyTaskType) return;` and only one plugin claims the event.
4. **Plugin runs.** The base class resolves input mappings against the workflow's variable scope, calls your `ExecuteAsync(...)`, projects outputs against the result, then calls `IWorkflowInstanceGrain.CompleteActivity` (or `FailActivity` on exception).
5. **Catalog tracks who's alive.** Each Worker silo announces its plugins to a Core-side `ICustomTaskCatalogGrain` at silo startup. The catalog reconciles every 30 s against `IManagementGrain.GetDetailedHosts()` and drops entries for silos no longer in the cluster. The management UI reads `GET /custom-tasks`.

## Authoring a plugin

A plugin is a .NET class library deriving from `CustomTaskHandlerBase`. The base class provides stream subscription, task-type filtering, and the success/failure callback paths; the author overrides `TaskType` and `ExecuteAsync` only. Plugin metadata is registered via `services.AddCustomTaskPlugin<THandler>(taskType, displayName?, parameterSchema?)` from the Worker silo's host.

For a step-by-step tutorial — project setup, handler, schema, DI wiring, BPMN authoring, and troubleshooting — see [Writing custom-task plugins](/guides/writing-custom-tasks/).

## Parameter types

Parameter schemas drive what the management UI's BPMN editor renders for each `<zeebe:input>` row. The five primitive types map to typed widgets:

| Type | Widget | Notes |
|---|---|---|
| `String` | Single-line text | Accepts a literal value or `=variableName` for a workflow-variable reference. |
| `Integer` | Number field | Whole numbers. |
| `Boolean` | Checkbox | `"true"` / `"false"` written as the source. |
| `Expression` | Multi-line text (monospace) | Always `=`-prefixed; evaluated against the workflow scope at dispatch time. |
| `MultilineString` | Textarea | For JSON request bodies, etc. |

Repeat-allowed parameters (e.g. REST headers — multiple `(key, value)` pairs) use:

| Type | Editor | Notes |
|---|---|---|
| `List` (with `ItemType`) | Single-column list with "+ Add" / "remove" | `ItemType` must be a primitive. |
| `Map` (with `ItemType`) | Two-column `(Key, Value)` table with "+ Add" / "remove" | Value column rendered per `ItemType`. |

Nested `List`/`Map` (a list whose items are themselves objects with three fields) is **not** supported in v1 — keep `ItemType` to a primitive (`String | Integer | Boolean | Expression | MultilineString`).

## Mapping grammar

Sources and targets in `<zeebe:input>` / `<zeebe:output>` follow this grammar (validated at BPMN deploy time):

| Form | Example | Meaning |
|------|---------|---------|
| `=identifier(.path)*` | `=order.total` | Walk through the variable scope |
| `="literal"` | `="GET"` | Quoted string literal |
| `=42` / `=3.14` / `=true` / `=false` / `=null` | `=true` | Primitive literal |
| Bare string | `application/json` | Treated as a string literal |

Targets must be valid identifiers (`^[a-zA-Z_][a-zA-Z0-9_]*$`). The target `__response` is **reserved** on `<zeebe:output>` — plugins write their result under that key in the dictionary they return, and your output mappings reference it via `=__response.body` or similar.

## What lives where

| Piece | Project | Purpose |
|-------|---------|---------|
| `InputMapping`, `OutputMapping`, `ExecuteCustomTaskEvent`, `CustomTaskFailedActivityException`, `ActivityException`, `ActivityErrorState` | `Fleans.Domain.Abstractions` | Pure data contracts crossing grain boundaries. Leaf NuGet — no dependencies beyond Orleans SDK. |
| `CustomTaskActivity` | `Fleans.Domain` | Domain aggregate for the custom-task activity (internal to the engine). |
| `MappingResolver`, `ICustomTaskCatalogGrain`, `CustomTaskRegistration`, `CustomTaskCatalogEntry`, `CustomTaskParameterSchema`, `IWorkflowInstanceCallback` | `Fleans.Application.Abstractions` | Plugin-author surface: schema records, grain interfaces Worker calls back into, mapping resolution utility. |
| `CustomTaskCatalogGrain` | `Fleans.Application` | Core-side catalog grain implementation. |
| `CustomTaskHandlerBase`, `CustomTaskPluginRegistrar`, `CustomTaskPluginDescriptor`, `AddCustomTaskPlugin<T>(…)` | `Fleans.Worker` | Worker-side base class your plugin extends, plus the lifecycle hook that announces plugins to the catalog. |
| `CustomTasksController` (`GET /custom-tasks`, `GET /custom-tasks/{type}`) | `Fleans.Api` | Reads the catalog for the management UI. |

## Failure semantics

- Throw `CustomTaskFailedActivityException(int code, string message)` from `ExecuteAsync` to fail the activity with a typed error. The code surfaces in the activity's `ErrorState.Code`, so workflow authors can route via boundary error events on specific codes.
- Any other exception fails the activity with code 500 (the standard `ActivityException` mapping).
- If `FailActivity` itself fails (e.g. the workflow grain is unavailable), the handler rethrows so the Orleans stream provider retries — domain idempotency guards handle the duplicate.

## Catalog & liveness

- Workers announce themselves once at silo startup via `ILifecycleParticipant<ISiloLifecycle>` at stage `Active`, with bounded retry (2 s, 5 s, 15 s) if the catalog grain is briefly unreachable.
- The catalog polls `IManagementGrain.GetDetailedHosts()` every 30 s and drops entries whose silo is no longer in `{Joining, Active, ShuttingDown}`. No heartbeats from workers.
- **Catalog state is persisted via EF Core** (table `CustomTaskCatalogEntries`, composite PK on `(TaskType, SiloName)`, parameter schema serialized as JSON). After a Core silo restart, the catalog reactivates with the persisted rows immediately, then the next reconcile pass drops anything whose silo left the cluster while Core was down. Worker silos that are still alive don't need to re-register — their entry survived.
- **Note for large fleets**: each `Register` call from a Worker silo persists synchronously. With 100+ Worker silos × multiple plugins each, expect a brief boot-time spike of catalog-blocked time at fleet boot. Single-host Aspire sees this as invisible.

## REST Caller (built-in plugin)

Fleans ships one custom-task plugin out of the box: `Fleans.Plugins.RestCaller`. It backs `<serviceTask type="rest-call">` and is wired into the API host by default (`services.AddRestCallerPlugin()`), so any workflow can call it.

### Parameters

| Name | Type | Required | Default | Notes |
|---|---|---|---|---|
| `url` | `String` | yes | — | Absolute URI |
| `method` | `String` | yes | `GET` | One of GET / POST / PUT / PATCH / DELETE / HEAD / OPTIONS |
| `headers` | `Map<String>` | no | `null` | Each `(name, value)` pair sent as a header. **v1 only sources from a workflow variable** (e.g. `<zeebe:input source="=requestHeaders" target="headers" />`). |
| `body` | `MultilineString` | no | `null` | Sent verbatim. If non-empty and no `Content-Type` header is supplied, defaults to `application/json` |
| `successCodes` | `List<Integer>` | no | `null` | When null/empty, defaults to `200..299`. Pass an explicit list (e.g. `[200, 201, 404]`) when you want non-2xx codes treated as success. **v1 only sources from a workflow variable.** |
| `timeoutSec` | `Integer` | yes | `30` | Whole seconds; clamped to `[1, 300]`. Timeout fails the activity with `code="504"` |
| `idempotencyKeyHeader` | `String` | no | `null` | When set, plugin sends `<header>: <activityInstanceId-guid>` so server-side dedupe is keyed on the activity instance id (mitigates retries under silo failure) |

### Failure semantics

| Outcome | `ErrorState.Code` | Message |
|---|---|---|
| HTTP status outside `successCodes` | `<status>` (e.g. `"404"`) | response body, truncated to 1024 chars |
| Network error (`HttpRequestException`) | `"502"` | `ex.Message` |
| Timeout (per `timeoutSec`) | `"504"` | `"timeout after Ns calling <uri>"` |
| Bad URL / unsupported method / `timeoutSec` out of `[1, 300]` | `"400"` | descriptive message |

Workflow authors route via boundary error events with `errorCode="404"`, `errorCode="504"`, etc.

### Worked example

```xml
<bpmn:serviceTask id="getUser" type="rest-call">
  <bpmn:extensionElements>
    <zeebe:ioMapping>
      <zeebe:input  source="=userApiUrl"        target="url" />
      <zeebe:input  source="GET"                target="method" />
      <zeebe:input  source="=requestHeaders"    target="headers" />
      <zeebe:input  source="10"                 target="timeoutSec" />
      <zeebe:output source="=__response.body"   target="user" />
      <zeebe:output source="=__response.statusCode" target="status" />
    </zeebe:ioMapping>
  </bpmn:extensionElements>
</bpmn:serviceTask>
```

Start the workflow with the variables that populate the inputs:

```json
{
  "WorkflowId": "fetch-user",
  "Variables": {
    "userApiUrl": "https://api.example.com/users/42",
    "requestHeaders": { "Authorization": "Bearer abc", "Accept": "application/json" }
  }
}
```

The activity completes with `user` (the parsed JSON body) and `status` (the integer status code) merged into the workflow scope. Author can route subsequent gateways on `status`.

### v1 limitations

- `headers` and `successCodes` (Map / List parameters) can only come from workflow variables. The mapping grammar doesn't support literal `=[200, 404]` or `={"X-Foo":"bar"}` syntax in BPMN. Authors who need static values seed them via `POST /Workflow/start` `Variables` or build them in a preceding `<scriptTask>`. The management UI editor (sub-issue C) is the long-term fix.
- No OAuth / mTLS / certificate auth — pass static `Authorization` headers.
- No HTTP-level retry — workflow authors retry via boundary error events.
- No streaming (SSE / WebSocket / chunked).

## Limitations

- Plugins are .NET assemblies referenced from the Worker silo's host project. Hot-loading is out of scope.
- Per-plugin placement filters (route `rest-call` only to silos with that plugin) are out of scope; today the Worker placement director routes any `[WorkerPlacement]` grain to any worker silo. Operators choose topology by DI-registering only the plugins they want on each worker.
- Per-task-type stream partitioning (one Orleans stream per `taskType`) is deferred — for now every plugin handler receives every event and discards mismatches with an `if (...) return;` early-out.

## Hosting plugins outside the engine repo

If you want to run your custom-task plugins in their own deployable (separate from the engine
image) — see the [Hosting Plugins (Custom Worker Host)](/fleans/guides/custom-worker-host/) guide.
The [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example)
GitHub template is the supported starting point; it references **only** `Fleans.Worker`
(via NuGet) + plugin assemblies, with no `Fleans.Application` / `Fleans.Domain` in the
dependency closure.

## Reference

- [Issue #357](https://github.com/nightBaker/fleans/issues/357) — design history (v1–v12).
- [Issue #448](https://github.com/nightBaker/fleans/issues/448) — `Fleans.CustomWorkerHost` worked example + NuGet packaging.
- The script-task event handler (`Fleans.Application/Events/Handlers/WorkflowExecuteScriptEventHandler.cs`) is the structural model `CustomTaskHandlerBase` follows.
- Manual test plans: `tests/manual/37-custom-task-framework/test-plan.md`, `tests/manual/40-custom-worker-host/test-plan.md`.
