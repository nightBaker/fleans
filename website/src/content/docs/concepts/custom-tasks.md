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

A minimal "say hi" plugin:

```csharp
using Fleans.Worker.CustomTasks;

public sealed class HiPluginHandler(
    ILogger<HiPluginHandler> logger,
    IGrainFactory grainFactory)
    : CustomTaskHandlerBase(logger, grainFactory)
{
    protected override string TaskType => "hi";
    protected override string? DisplayName => "Say hi";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CancellationToken cancellationToken)
    {
        var name = resolvedInputs.TryGetValue("name", out var n) ? n?.ToString() : "world";
        return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["__response"] = $"hi, {name}!",
        });
    }
}
```

`[ImplicitStreamSubscription]` and `[WorkerPlacement]` are inherited from the base class — you do not need to repeat them on the subclass.

Register the plugin in the Worker silo's host:

```csharp
services.AddCustomTaskPlugin<HiPluginHandler>(
    taskType: "hi",
    displayName: "Say hi",
    parameterSchema: new CustomTaskParameterSchema(new[]
    {
        new CustomTaskParameterSpec(
            Name: "name",
            DisplayName: "Recipient name",
            Type: CustomTaskParameterType.String,
            Required: false,
            Description: "Greeting target; defaults to \"world\".",
            DefaultValue: "world"),
    }));
```

The `parameterSchema` is optional. When supplied, the management UI's BPMN editor (sub-issue C) renders a typed editor for each parameter (`String` → text field, `Boolean` → checkbox, `Expression` → multi-line `=`-prefixed input, etc.). Pass `CustomTaskParameterSchema.Empty` for plugins that take no inputs; omit the argument entirely to leave the editor opaque (UI falls back to a free-form key/value editor).

### Repeat-allowed parameters: `List` and `Map`

Parameters that accept multiple values (e.g. a REST plugin's HTTP headers, where each header is a separate `(key, value)` pair) use `Type = List` or `Type = Map` plus an `ItemType` describing each entry. `List` is "N values of `ItemType`"; `Map` is "N `(string-key, ItemType-value)` entries":

```csharp
services.AddCustomTaskPlugin<RestCallerHandler>(
    taskType: "rest-call",
    displayName: "REST Caller",
    parameterSchema: new CustomTaskParameterSchema(new[]
    {
        new CustomTaskParameterSpec("url", "URL",
            CustomTaskParameterType.String,
            Required: true, Description: null, DefaultValue: null),

        // multiple (header-name, header-value) pairs
        new CustomTaskParameterSpec("headers", "HTTP Headers",
            CustomTaskParameterType.Map,
            Required: false, Description: "Repeat for each header.", DefaultValue: null,
            ItemType: CustomTaskParameterType.String),

        // multiple HTTP status codes treated as success
        new CustomTaskParameterSpec("successCodes", "Success Codes",
            CustomTaskParameterType.List,
            Required: false, Description: "Defaults to 200..299.", DefaultValue: null,
            ItemType: CustomTaskParameterType.Integer),
    }));
```

The editor renders `Map` as a two-column table (Key, Value) with an "+ Add row" button; `List` as a single-column list with "+ Add" / "remove". The value column is rendered per `ItemType`. Nested `List`/`Map` (e.g. a list whose items are themselves objects with three fields) is **not** supported in v1 — keep `ItemType` to a primitive (`String | Integer | Boolean | Expression | MultilineString`).

Reference your plugin from BPMN:

```xml
<bpmn:serviceTask id="greet" type="hi">
  <bpmn:extensionElements>
    <zeebe:ioMapping>
      <zeebe:input  source="=customerName" target="name" />
      <zeebe:output source="=__response"   target="greeting" />
    </zeebe:ioMapping>
  </bpmn:extensionElements>
</bpmn:serviceTask>
```

When the workflow reaches `greet`, the plugin runs, and the variable `greeting` ends up holding `"hi, alice!"` (assuming `customerName` was `"alice"`).

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
