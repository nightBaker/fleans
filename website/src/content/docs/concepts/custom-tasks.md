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
| `CustomTaskActivity`, `InputMapping`, `OutputMapping`, `ExecuteCustomTaskEvent`, `CustomTaskFailedActivityException` | `Fleans.Domain` | Pure data contracts crossing grain boundaries. |
| `MappingResolver`, `ICustomTaskCatalogGrain`, `CustomTaskCatalogGrain`, `CustomTaskRegistration`, `CustomTaskCatalogEntry` | `Fleans.Application` | Core-side catalog grain; the only Core-hosted custom-task piece. |
| `CustomTaskHandlerBase`, `CustomTaskPluginRegistrar`, `CustomTaskPluginDescriptor`, `AddCustomTaskPlugin<T>(…)` | `Fleans.Worker` | Worker-side base class your plugin extends, plus the lifecycle hook that announces plugins to the catalog. |
| `CustomTasksController` (`GET /custom-tasks`, `GET /custom-tasks/{type}`) | `Fleans.Api` | Reads the catalog for the management UI. |

## Failure semantics

- Throw `CustomTaskFailedActivityException(int code, string message)` from `ExecuteAsync` to fail the activity with a typed error. The code surfaces in the activity's `ErrorState.Code`, so workflow authors can route via boundary error events on specific codes.
- Any other exception fails the activity with code 500 (the standard `ActivityException` mapping).
- If `FailActivity` itself fails (e.g. the workflow grain is unavailable), the handler rethrows so the Orleans stream provider retries — domain idempotency guards handle the duplicate.

## Catalog & liveness (v1)

- Workers announce themselves once at silo startup via `ILifecycleParticipant<ISiloLifecycle>` at stage `Active`, with bounded retry (2 s, 5 s, 15 s) if the catalog grain is briefly unreachable.
- The catalog polls `IManagementGrain.GetDetailedHosts()` every 30 s and drops entries whose silo is no longer in `{Joining, Active, ShuttingDown}`. No heartbeats from workers.
- Catalog state is in-memory only in v1. After a Core silo restart, the catalog repopulates as Worker silos restart and re-register; persistence (so the UI is correct immediately after Core restart) is a v2 follow-up.

## Limitations

- Plugins are .NET assemblies referenced from the Worker silo's host project. Hot-loading is out of scope.
- Per-plugin placement filters (route `rest-call` only to silos with that plugin) are out of scope; today the Worker placement director routes any `[WorkerPlacement]` grain to any worker silo. Operators choose topology by DI-registering only the plugins they want on each worker.
- Per-task-type stream partitioning (one Orleans stream per `taskType`) is deferred — for now every plugin handler receives every event and discards mismatches with an `if (...) return;` early-out.

## Reference

- [Issue #357](https://github.com/nightBaker/fleans/issues/357) — design history (v1–v12).
- The script-task event handler (`Fleans.Application/Events/Handlers/WorkflowExecuteScriptEventHandler.cs`) is the structural model `CustomTaskHandlerBase` follows.
- Manual test plan: `tests/manual/37-custom-task-framework/test-plan.md`.
