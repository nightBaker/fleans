---
title: Custom Tasks
description: Plug your own service tasks into Fleans by writing a worker-side handler that subscribes to ExecuteCustomTaskEvent and self-filters by task type.
---

A **custom task** is a `<bpmn:serviceTask type="...">` whose execution is supplied by a plugin you write. The plugin lives on a Worker silo as a grain that derives from `CustomTaskHandlerBase`; the engine does not need to know what your plugin does — only that *some* worker can claim the `type`.

This is the recommended pattern for anything Fleans doesn't ship in the box: REST calls, email, Slack, custom database queries, file uploads, etc.

## How it works

1. **BPMN parsing.** The converter sees `<serviceTask type="rest-call">` (or the Camunda equivalent `<extensionElements><zeebe:taskDefinition type="rest-call"/></extensionElements>`) and produces a `CustomTaskActivity` carrying the task type and any `<fleans:ioMapping>` declarations.
2. **Activity emits an event.** When the workflow reaches the activity, it publishes `ExecuteCustomTaskEvent { TaskType, InputMappings, OutputMappings, VariablesId, … }` on the per-type Orleans stream `events.ExecuteCustomTaskEvent.{TaskType}` — partitioned by `TaskType` so each plugin's handler grain class only receives events it actually claims.
3. **Plugin handler is dispatched directly.** Each plugin's `CustomTaskHandlerBase` subclass implicit-subscribes to its own per-type namespace (`[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<task-type>")]`), so Orleans activates only the matching plugin's grain. No filter-after-deliver waste: silos no longer deserialize events for plugins they don't host.
4. **Plugin runs.** The base class resolves input mappings against the workflow's variable scope, calls your `ExecuteAsync(...)`, projects outputs against the result, then calls `IWorkflowInstanceGrain.CompleteActivity` (or `FailActivity` on exception).
5. **Catalog tracks who's alive.** Each Worker silo announces its plugins to a Core-side `ICustomTaskCatalogGrain` at silo startup. The catalog reconciles every 30 s against `IManagementGrain.GetDetailedHosts()` and drops entries for silos no longer in the cluster. The management UI reads `GET /custom-tasks`.

## Authoring a plugin

A plugin is a .NET class library deriving from `CustomTaskHandlerBase`. The base class provides stream subscription, error handling, and the success/failure callback paths; the author overrides `TaskType` and `ExecuteAsync` only. The concrete subclass MUST carry `[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<task-type>")]` as a literal string — attribute arguments must be compile-time constants, so the literal cannot be derived from `TaskType` at the attribute site. Plugin metadata is registered via `services.AddCustomTaskPlugin<THandler>(taskType, displayName?, parameterSchema?)` from the Worker silo's host. The registration call validates at silo startup that (a) no other handler already claims `taskType` and (b) the `[ImplicitStreamSubscription]` string on `THandler` matches the per-type namespace — both throw `InvalidOperationException` immediately on drift.

For a step-by-step tutorial — project setup, handler, schema, DI wiring, BPMN authoring, and troubleshooting — see [Writing custom-task plugins](/guides/writing-custom-tasks/).

## Parameter types

Parameter schemas drive what the management UI's BPMN editor renders for each `<fleans:input>` row. The five primitive types map to typed widgets:

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

Sources and targets in `<fleans:input>` / `<fleans:output>` follow this grammar (validated at BPMN deploy time):

| Form | Example | Meaning |
|------|---------|---------|
| `=identifier(.path)*` | `=order.total` | Walk through the variable scope |
| `="literal"` | `="GET"` | Quoted string literal |
| `=42` / `=3.14` / `=true` / `=false` / `=null` | `=true` | Primitive literal |
| Bare string | `application/json` | Treated as a string literal |

Targets must be valid identifiers (`^[a-zA-Z_][a-zA-Z0-9_]*$`). The target `__response` is **reserved** on `<fleans:output>` — plugins write their result under that key in the dictionary they return, and your output mappings reference it via `=__response.body` or similar.

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
| `headers` | `Map<String>` | no | `null` | Each `(name, value)` pair sent as a header. **v1 only sources from a workflow variable** (e.g. `<fleans:input source="=requestHeaders" target="headers" />`). |
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
<!-- Requires xmlns:fleans="https://fleans.io/schema/bpmn/1.0" on <bpmn:definitions> -->
<bpmn:serviceTask id="getUser" type="rest-call">
  <bpmn:extensionElements>
    <fleans:ioMapping>
      <fleans:input  source="=userApiUrl"        target="url" />
      <fleans:input  source="GET"                target="method" />
      <fleans:input  source="=requestHeaders"    target="headers" />
      <fleans:input  source="10"                 target="timeoutSec" />
      <fleans:output source="=__response.body"   target="user" />
      <fleans:output source="=__response.statusCode" target="status" />
    </fleans:ioMapping>
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

**Authoring in the editor.** When you select a plugin on a `<bpmn:serviceTask>` in `/editor`, the properties panel renders typed input fields driven by the plugin's `CustomTaskParameterSchema` (so for REST Caller you see `URL`, `Method`, `Timeout`, …). **Output mappings stay free-form** — the schema doesn't enumerate possible outputs because plugin results are dynamic (HTTP response bodies, SQL query shapes, etc.). Click *Add Output Mapping* to capture any key the plugin returns. By framework convention, plugin handlers expose their result under the reserved `__response` key (see the *I/O mappings* section above for the rules); the worked example uses `source="=__response.body"` / `source="=__response.statusCode"` for that reason.

### v1 limitations

- `headers` and `successCodes` (Map / List parameters) can only come from workflow variables. The mapping grammar doesn't support literal `=[200, 404]` or `={"X-Foo":"bar"}` syntax in BPMN. Authors who need static values seed them via `POST /Workflow/start` `Variables` or build them in a preceding `<scriptTask>`. The management UI editor (sub-issue C) is the long-term fix.
- No OAuth / mTLS / certificate auth — pass static `Authorization` headers.
- No HTTP-level retry — workflow authors retry via boundary error events.
- No streaming (SSE / WebSocket / chunked).

## Cancellation

Plugins receive a `CancellationToken` whose source is the handler grain's lifetime. When Orleans deactivates the grain (silo scale-down, idle collection, shutdown), the token is signalled and the in-flight `ExecuteAsync` should propagate `OperationCanceledException`. The base class catches that case, **does not** fail the activity, and lets the stream provider redeliver the event after the grain reactivates elsewhere. Plugins that perform long-running I/O should thread the token through (`HttpClient.SendAsync(request, ct)`, `Task.Delay(timeout, ct)`, etc.).

Plugins that **ignore** the token block silo deactivation until Orleans's hard timeout expires (Orleans 10.x default: 30 s graceful, then force-kill). The stream event is **not lost** — at-least-once delivery causes the stream provider to redeliver to the next handler activation. The cost of ignoring the token is silo-shutdown latency, not correctness.

Plugin authors should also know that an `OperationCanceledException` whose `CancellationToken` is **not** the supplied grain-lifetime token (e.g., a plugin-internal timeout that fires before the silo deactivates) is treated as a regular plugin failure and routes to `FailActivity` with code 500 — the base class only re-routes cancellation that originates from grain deactivation. See `RestCallerHandler` for the canonical layered-CTS pattern: the plugin's own per-request timeout is linked with the supplied token via `CancellationTokenSource.CreateLinkedTokenSource`.

## Limitations

- Plugins are .NET assemblies referenced from the Worker silo's host project. Hot-loading is out of scope.
- Per-plugin placement filters (route `rest-call` only to silos with that plugin) are out of scope; today the Worker placement director routes any `[WorkerPlacement]` grain to any worker silo. Operators choose topology by DI-registering only the plugins they want on each worker.

## Upgrade note: per-type stream namespaces

The publisher routes `ExecuteCustomTaskEvent` to `events.ExecuteCustomTaskEvent.{TaskType}` per the partitioning above. Pre-v1, an engine upgrade that introduces this change orphans any `ExecuteCustomTaskEvent`s already enqueued on the previous shared `events.ExecuteCustomTaskEvent` stream — no subscriber exists for them after rollout. Operators should expect any in-flight custom tasks across the upgrade window to stall; no formal drain procedure is shipped pre-v1.

Plugin authors hosting from the [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example) template need to update their handlers: each concrete `CustomTaskHandlerBase` subclass must carry `[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<task-type>")]` with a literal string matching its `TaskType`. The Worker host's `AddCustomTaskPlugin<T>(taskType, …)` call now throws `InvalidOperationException` at startup if the attribute is missing or drifted, or if two plugins claim the same `TaskType` — diagnose against the exception message.

## Hosting plugins externally

For production plugin estates, run your plugins in a dedicated Worker silo separate from
the engine's `Fleans.Api` / `Fleans.Web` / `Fleans.WorkerHost` images. This is the recommended
deployment pattern for non-trivial plugin estates.

See [Hosting plugins externally](/fleans/concepts/plugin-hosting/) for the architecture,
the `Plugin` role, isolation guarantees, the `AddFleansPluginHost` one-liner, and the
scaffolding template.

## Reference

- [Issue #357](https://github.com/nightBaker/fleans/issues/357) — design history (v1–v12).
- [Issue #448](https://github.com/nightBaker/fleans/issues/448) — `Fleans.CustomWorkerHost` worked example + NuGet packaging.
- The script-task event handler (`Fleans.Application/Events/Handlers/WorkflowExecuteScriptEventHandler.cs`) is the structural model `CustomTaskHandlerBase` follows.
- Manual test plans: `tests/manual/37-custom-task-framework/test-plan.md`, `tests/manual/40-custom-worker-host/test-plan.md`.
