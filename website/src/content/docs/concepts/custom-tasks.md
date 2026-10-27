---
title: Custom Tasks
description: Plug your own service tasks into Fleans by writing a worker-side handler that subscribes to ExecuteCustomTaskEvent and self-filters by task type.
---

A **custom task** is a `<bpmn:serviceTask type="...">` whose execution is supplied by a plugin you write. The plugin lives on a Worker silo as a grain that derives from `CustomTaskHandlerBase`; the engine does not need to know what your plugin does — only that *some* worker can claim the `type`.

This is the recommended pattern for anything Fleans doesn't ship in the box: REST calls, email, Slack, custom database queries, file uploads, etc.

## How it works

1. **BPMN parsing.** The converter sees `<serviceTask type="rest-call">` (or the Camunda equivalent `<extensionElements><zeebe:taskDefinition type="rest-call"/></extensionElements>`) and produces a `CustomTaskActivity` carrying the task type and any `<fleans:ioMapping>` declarations.
2. **Engine maps inputs, then emits an event.** When the workflow reaches the activity, the engine evaluates its `<fleans:input>` mappings into the task's local scope and publishes `ExecuteCustomTaskEvent { TaskType, Inputs, VariablesId, … }` on the per-type Orleans stream `events.ExecuteCustomTaskEvent.{TaskType}` — partitioned by `TaskType` so each plugin's handler grain class only receives events it actually claims.
3. **Plugin handler is dispatched directly.** Each plugin's `CustomTaskHandlerBase` subclass implicit-subscribes to its own per-type namespace (`[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<task-type>")]`), so Orleans activates only the matching plugin's grain. No filter-after-deliver waste: silos no longer deserialize events for plugins they don't host.
4. **Plugin runs.** The base class calls your `ExecuteAsync(...)` with the already-resolved inputs, then reports the dictionary it returns through `IWorkflowInstanceGrain.CompleteActivity` (or `FailActivity` on exception). The engine evaluates the `<fleans:output>` mappings against that result: only the mapped outputs reach the workflow scope, or, when the task has no outputs, the whole result does.
5. **Catalog tracks who's alive.** Each Worker silo announces its plugins to a Core-side `ICustomTaskCatalogGrain` at silo startup. The catalog reconciles every 30 s against `IManagementGrain.GetDetailedHosts()` and drops entries for silos no longer in the cluster. The management UI reads `GET /custom-tasks`.

<figure class="arch-diagram" style="--arch-ratio: 560 / 678; max-width: 600px; margin-inline: auto">
  <iframe data-arch-src="/fleans/diagrams/custom-task-dispatch.html" src="/fleans/diagrams/custom-task-dispatch.html?embed=1" title="Custom task dispatch sequence" loading="lazy"></iframe>
  <figcaption>The engine sends work over a per-task-type Orleans stream; the handler grain reports back by calling the workflow instance directly, and a typed failure code can be caught by an error boundary. <a href="/fleans/diagrams/custom-task-dispatch.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

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

`<fleans:input>` / `<fleans:output>` on a custom task are the engine's general
[input/output mappings](/fleans/concepts/activities/io-mapping/): a `source` starting with `=` is a
C# expression over the workflow variables (`=order.total`, `=order.total * 1.12`), anything else is
a string literal, and `target` is a plain identifier (`^[a-zA-Z_][a-zA-Z0-9_]*$`). The engine
evaluates them, so plugins never see the mapping expressions: inputs arrive resolved, and outputs
read the plugin's result. By convention plugins return their result under `__response`, so outputs
read e.g. `=__response.body`.

An input or output that does not evaluate fails the task with code 500 before (or instead of) the
plugin's result being applied.

## What lives where

| Piece | Project | Purpose |
|-------|---------|---------|
| `ExecuteCustomTaskEvent`, `InputMapping`, `OutputMapping`, `CustomTaskFailedActivityException`, `ActivityException`, `ActivityErrorState` | `Fleans.Domain.Abstractions` | Pure data contracts crossing grain boundaries. Leaf NuGet — no dependencies beyond Orleans SDK. |
| `CustomTaskActivity` | `Fleans.Domain` | Domain aggregate for the custom-task activity (internal to the engine). |
| `ICustomTaskCatalogGrain`, `CustomTaskRegistration`, `CustomTaskCatalogEntry`, `CustomTaskParameterSchema`, `IWorkflowInstanceCallback` | `Fleans.Application.Abstractions` | Plugin-author surface: schema records and the grain interfaces Worker calls back into. |
| `CustomTaskCatalogGrain` | `Fleans.Application` | Core-side catalog grain implementation. |
| `CustomTaskHandlerBase`, `CustomTaskPluginRegistrar`, `CustomTaskPluginDescriptor`, `AddCustomTaskPlugin<T>(…)` | `Fleans.Worker` | Worker-side base class your plugin extends, plus the lifecycle hook that announces plugins to the catalog. |
| `CustomTasksController` (`GET /custom-tasks`, `GET /custom-tasks/{type}`) | `Fleans.Api` | Reads the catalog for the management UI. |

## Failure semantics

- Throw `CustomTaskFailedActivityException(int code, string message)` from `ExecuteAsync` to fail the activity with a typed error. The code surfaces in the activity's `ErrorState.Code`, so workflow authors can route via boundary error events on specific codes.
- Any other exception fails the activity with code 500 (the standard `ActivityException` mapping).
- Handlers that call the workflow directly through `IWorkflowInstanceCallback` (rather than `CustomTaskHandlerBase`) can pass variables with the failure: `FailActivity(activityId, activityInstanceId, exception, variables)` makes them visible to whatever catches the error: an error boundary event on the activity or an enclosing sub-process, or an error event sub-process (in its handler scope). They are **not** forwarded to a parent workflow, so an error boundary on a call activity does not see them. They stay on the failed activity's scope when nothing catches the error. A redelivered call for an activity that already failed is ignored, variables included.
- If no boundary error event or error event sub-process catches the failure and it leaves the instance with no active activities, the **instance** terminates as Failed (`isFailed: true` on the state snapshot) — see [Unhandled errors fail the instance](/fleans/guides/error-handling/#unhandled-errors-fail-the-instance).
- A plugin that is never registered does **not** fail the activity: with nobody subscribed to the per-type stream, the activity stays Active until a plugin for that `type` comes online or someone calls `POST /Execution/complete-activity`.
- If `FailActivity` itself fails (e.g. the workflow grain is unavailable), the handler rethrows so the Orleans stream provider retries — domain idempotency guards handle the duplicate.
- A plugin that succeeded is never recorded as failed because reporting its result failed: an infrastructure failure of `CompleteActivity` is rethrown for redelivery (logged as EventId 4039), which runs the plugin again (see [Running many workers](#running-many-workers)).

## Running many workers

You can run any number of worker / plugin-host replicas side by side; they process custom tasks of different workflow instances in parallel:

- Events are routed by workflow instance: each instance's custom tasks go to a single handler activation on one silo at a time, while different instances spread across all replicas.
- Before calling your `ExecuteAsync`, the handler reads the activity's variables from the workflow instance, and gets them only while that activity instance is still active. A redelivered or duplicate event for an activity that already completed or failed is skipped (logged as EventId 4038), so a finished task is never executed again.
- A duplicate that arrives while the task is still running waits for the in-flight run on the same activation and is then skipped.
- If that read fails for an infrastructure reason (the engine silo is restarting, the call times out), your plugin is not called and the activity is not failed: the event is redelivered. Any other error there (for example a missing variable scope) fails the activity, since redelivery could not fix it.
- **Your plugin can still run more than once for the same task**: when it is interrupted mid-run (a worker that dies, or a handler activation deactivated by graceful shutdown or rebalancing while `ExecuteAsync` runs); when it succeeded but reporting the result to the engine failed for an infrastructure reason; and, rarely, when a cluster membership change briefly leaves two handler activations that both see the task as active (the check is a read, not a lock). The event is redelivered and the task runs again, and a plugin that ignores the `CancellationToken` may even keep running on the old silo in parallel. Honour the token, and use `context.ActivityInstanceId` as an idempotency key for external calls (the REST Caller plugin does this when you set its `idempotencyKeyHeader` input).

## Catalog & liveness

- Workers announce themselves once at silo startup via `ILifecycleParticipant<ISiloLifecycle>` at stage `Active`, with bounded retry (2 s, 5 s, 15 s) if the catalog grain is briefly unreachable — including while no Core silo has joined yet, so a worker or plugin host may start before Core. If every attempt fails, the silo still starts; only the catalog UI misses that plugin until the next restart.
- The catalog polls `IManagementGrain.GetDetailedHosts()` every 30 s and drops entries whose silo is no longer in `{Joining, Active, ShuttingDown}`. No heartbeats from workers.
- **Catalog state is persisted via EF Core** (table `CustomTaskCatalogEntries`, composite PK on `(TaskType, SiloName)`, parameter schema serialized as JSON). After a Core silo restart, the catalog reactivates with the persisted rows immediately, then the next reconcile pass drops anything whose silo left the cluster while Core was down. Worker silos that are still alive don't need to re-register — their entry survived.
- **Note for large fleets**: each `Register` call from a Worker silo persists synchronously. With 100+ Worker silos × multiple plugins each, expect a brief boot-time spike of catalog-blocked time at fleet boot. Single-host Aspire sees this as invisible.

## Ready-made plugins (fleans-plugins)

The engine images don't bundle any plugin. Ready-made plugins live in the separate
[fleans-plugins](https://github.com/nightBaker/fleans-plugins) repository, released in step
with Fleans — each as a NuGet package for your own host, and all of them together in one
ready-to-run Plugin-role host image, `ghcr.io/nightbaker/fleans-plugins`:

| Plugin | Task type | NuGet package | Guide |
|---|---|---|---|
| REST Caller — outbound HTTP call, response mapped back into the workflow | `rest-call` | `Fleans.Plugins.RestCaller` | [REST Caller guide](https://github.com/nightBaker/fleans-plugins/blob/main/docs/rest-caller/README.md) |
| Camunda 7 external tasks — see [below](#camunda-7-external-tasks) | `camunda:<topic>` | `Fleans.Plugins.CamundaExternalTask` | [Camunda guide](https://github.com/nightBaker/fleans-plugins/blob/main/docs/camunda-external-task/README.md) |

Run the host image next to the engine (it joins the cluster in the `Plugin` role — see
[Hosting plugins externally](/fleans/concepts/plugin-hosting/)), or reference the package from
your own worker host. Each plugin's parameters, failure codes and examples are in its guide.

**Authoring in the editor.** When you select a plugin on a `<bpmn:serviceTask>` in `/editor`, the properties panel renders typed input fields driven by the plugin's `CustomTaskParameterSchema` (so for REST Caller you see `URL`, `Method`, `Timeout`, …). **Output mappings stay free-form** — the schema doesn't enumerate possible outputs because plugin results are dynamic (HTTP response bodies, SQL query shapes, etc.). Click *Add Output Mapping* to capture any key the plugin returns. By framework convention, plugin handlers expose their result under the reserved `__response` key (see [Mapping grammar](#mapping-grammar)), so output mappings read e.g. `source="=__response.body"`.

## Cancellation

Plugins receive a `CancellationToken` whose source is the handler grain's lifetime. When Orleans deactivates the grain (silo scale-down, idle collection, shutdown), the token is signalled and the in-flight `ExecuteAsync` should propagate `OperationCanceledException`. The base class catches that case, **does not** fail the activity, and lets the stream provider redeliver the event after the grain reactivates elsewhere. Plugins that perform long-running I/O should thread the token through (`HttpClient.SendAsync(request, ct)`, `Task.Delay(timeout, ct)`, etc.).

Plugins that **ignore** the token block silo deactivation until Orleans's hard timeout expires (Orleans 10.x default: 30 s graceful, then force-kill). The stream event is **not lost** — at-least-once delivery causes the stream provider to redeliver to the next handler activation. The cost of ignoring the token is silo-shutdown latency, not correctness.

Plugin authors should also know that an `OperationCanceledException` whose `CancellationToken` is **not** the supplied grain-lifetime token (e.g., a plugin-internal timeout that fires before the silo deactivates) is treated as a regular plugin failure and routes to `FailActivity` with code 500 — the base class only re-routes cancellation that originates from grain deactivation. Use a layered token: link the plugin's own per-request timeout with the supplied token via `CancellationTokenSource.CreateLinkedTokenSource` (the REST Caller plugin in fleans-plugins is a worked example).

## Limitations

- Plugins are .NET assemblies referenced from the Worker silo's host project. Hot-loading is out of scope.
- Per-plugin placement filters (route a task type only to silos with that plugin) are out of scope; today the `[WorkerPlacement]` placement filter allows any `[WorkerPlacement]` grain on any worker silo. Operators choose topology by DI-registering only the plugins they want on each worker.

## Upgrade note: upgrade the engine before workers

Custom-task handlers call the workflow instance's `GetVariablesIfActive` before running a plugin. An engine silo from a release without that method cannot answer it, so during a rolling upgrade **roll the engine (core) silos first, then worker / plugin hosts**. If a newer worker meets an older engine anyway, its custom tasks do not run until the engine is upgraded: depending on how the older engine rejects the call they either fail or are redelivered until the stream provider's redelivery window ends and then stay Active (complete them via `POST /Execution/complete-activity`).

## Upgrade note: handler grains named after the task type

Fleans now names each handler's grain `custom-task.<task-type>` instead of the handler's class name, so plugin hosts whose handlers share a class name no longer receive each other's tasks (which then hung). Plugin code does not change — rebuild against the new `Fleans.Worker`. **Stop the old plugin hosts before starting the new ones:** while an old and a new host of the same plugin run side by side, each task reaches both (one per grain name) and runs twice.

## Upgrade note: per-type stream namespaces

The publisher routes `ExecuteCustomTaskEvent` to `events.ExecuteCustomTaskEvent.{TaskType}` per the partitioning above. Pre-v1, an engine upgrade that introduces this change orphans any `ExecuteCustomTaskEvent`s already enqueued on the previous shared `events.ExecuteCustomTaskEvent` stream — no subscriber exists for them after rollout. Operators should expect any in-flight custom tasks across the upgrade window to stall; no formal drain procedure is shipped pre-v1.

Plugin authors hosting from the [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example) template need to update their handlers: each concrete `CustomTaskHandlerBase` subclass must carry `[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<task-type>")]` with a literal string matching its `TaskType`. The Worker host's `AddCustomTaskPlugin<T>(taskType, …)` call now throws `InvalidOperationException` at startup if the attribute is missing or drifted, or if two plugins claim the same `TaskType` — diagnose against the exception message.

## Camunda 7 external tasks

The Camunda external-task plugin from [fleans-plugins](https://github.com/nightBaker/fleans-plugins)
keeps Camunda 7 external-task workers running unchanged. It queues
`<serviceTask type="camunda:<topic>">` tasks and serves the Camunda `/engine-rest/external-task`
REST API. See [Migrating Camunda 7 External Tasks](/fleans/guides/camunda-external-tasks/).

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
- Manual test plans: `tests/manual/37-custom-task-framework/test-plan.md`, `tests/manual/40-custom-worker-host/test-plan.md`.
