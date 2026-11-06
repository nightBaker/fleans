---
title: Custom Tasks
description: Back your own service tasks with handlers that run on gRPC workers. The worker connects to the Fleans API, receives each task, and reports the outcome.
---

A **custom task** is a `<bpmn:serviceTask type="...">` whose execution is supplied by code you write. That code runs on a **worker**: a separate process that connects to the Fleans API over gRPC, receives the tasks of the types it implements, and reports each outcome. The worker needs only the API's URL. It does not join the Orleans cluster, and it has no Orleans dependency.

This is the pattern for anything Fleans doesn't ship in the box: REST calls, email, Slack, custom database queries, file uploads. A packaged handler is often called a *plugin*, as in the [ready-made plugins](#ready-made-plugins-fleans-plugins).

## How it works

1. **BPMN parsing.** The converter sees `<serviceTask type="rest-call">` (or the Camunda equivalent `<extensionElements><zeebe:taskDefinition type="rest-call"/></extensionElements>`) and produces a custom-task activity carrying the task type and any `<fleans:ioMapping>` declarations.
2. **The engine maps inputs and queues the task.** When the workflow reaches the activity, the engine evaluates its `<fleans:input>` mappings and publishes the task on a durable stream.
3. **A connected worker receives it.** The engine picks a connected worker of that type at random and sends it the resolved inputs and the visible variables.
4. **Your handler runs.** It returns a result, or fails. The worker reports the outcome and the engine evaluates the `<fleans:output>` mappings against the result: only the mapped outputs reach the workflow scope, or, when the task has no outputs, the whole result does.
5. **The catalog tracks who's connected.** Workers announce their task types, parameter schemas and access rules when they subscribe. The management UI reads `GET /custom-tasks`.

<figure class="arch-diagram" style="--arch-ratio: 560 / 678; max-width: 600px; margin-inline: auto">
  <iframe data-arch-src="/fleans/diagrams/custom-task-dispatch.html" src="/fleans/diagrams/custom-task-dispatch.html?embed=1" title="Custom task dispatch sequence" loading="lazy"></iframe>
  <figcaption>The engine hands the task to a connected worker over gRPC and waits for its report; a failure code can be caught by an error boundary. <a href="/fleans/diagrams/custom-task-dispatch.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

## Authoring a handler

In .NET, reference the [`Fleans.ExternalTasks.Sdk`](https://www.nuget.org/packages/Fleans.ExternalTasks.Sdk) package, derive from `ExternalTaskHandler`, and override `ExecuteAsync`. Register it with `services.AddExternalTaskHandler<THandler>(taskType, displayName?, parameterSchema?)` next to `services.AddFleansExternalTaskWorker()`. In any other language, generate a client from the protocol file: see [External Workers](/fleans/guides/external-workers/#other-languages).

For a step-by-step tutorial, see [Writing custom tasks](/fleans/guides/writing-custom-tasks/). To run workers in production, see [Hosting workers](/fleans/guides/custom-worker-host/).

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
evaluates them, so handlers never see the mapping expressions: inputs arrive resolved, and outputs
read the handler's result. By convention handlers return their result under `__response`, so outputs
read e.g. `=__response.body`.

An input or output that does not evaluate fails the task with code 500 before (or instead of) the
handler's result being applied.

## Failure semantics

- Throw `ExternalTaskFailedException(string code, string message)` from `ExecuteAsync` to fail the activity with a BPMN error code. The code surfaces in the activity's `ErrorState.Code`, so workflow authors can route via boundary error events on specific codes. Over the protocol this is `FailTask`.
- Any other exception fails the activity with code `500`.
- If no boundary error event or error event sub-process catches the failure and it leaves the instance with no active activities, the **instance** terminates as Failed (`isFailed: true` on the state snapshot) — see [Unhandled errors fail the instance](/fleans/guides/error-handling/#unhandled-errors-fail-the-instance).
- A task type with no connected worker does **not** fail the activity. It waits for a worker, within the limit described in [Delivery contract](/fleans/guides/external-workers/#delivery-contract).
- A handler that succeeded is never recorded as failed because reporting its result failed: an infrastructure failure on the engine side delivers the task again.

## Running many workers

You can run any number of worker processes side by side; they process tasks in parallel, including the tasks of one instance:

- Every task is dispatched on its own, so parallel branches and parallel multi-instance tasks of one workflow instance run at the same time and spread across all workers. Tasks run in no particular order relative to each other.
- Each worker process runs up to `MaxConcurrentTasks` tasks at once (32 by default). Fleans has no limit per task type across workers: if the system your handler calls has a rate limit, enforce it in the handler.
- Before offering a task, the engine reads the activity's variables, and gets them only while that activity instance is still active. A redelivered or duplicate task for an activity that already completed or failed is skipped (logged as EventId 14508), so a finished task is never executed again.
- **Handlers must be idempotent, because a task can still run more than once**: when its worker disconnects or crashes mid-run; when it reports nothing within the execution timeout; when it succeeded but the engine failed to record that for an infrastructure reason; and, rarely, during a cluster membership change. Honour the `CancellationToken`, and use `context.ActivityInstanceId`, which stays the same across deliveries, as an idempotency key for external calls (the REST Caller plugin does this when you set its `idempotencyKeyHeader` input).

## Catalog & liveness

- A worker's task types enter the catalog when it subscribes, and leave when it disconnects or its lease expires (`Fleans:ExternalTasks:WorkerTimeout`, 45 seconds by default).
- `siloNames` in `GET /custom-tasks` lists the connected workers by `WorkerName`.
- **Catalog state is persisted** (table `CustomTaskCatalogEntries`). After a Core silo restart the catalog starts from the stored rows and drops those whose worker has not reconnected within `WorkerTimeout`.
- A `<scheme>:*` wildcard subscription is not listed: there is no single task type to pick in the editor.

## Categories and private plugins

A task type can declare a **category**, which groups it in the editor's plugin picker. Task types without one go to the *Other* group.

A task type is **public** by default: any process can use it. A **private** one can only be used by the processes on its **allow-list**, matched exactly by process definition key.

```csharp
services.AddExternalTaskHandler<ChargeHandler>(
    taskType: "charge-card",
    displayName: "Charge card",
    category: "Payments",
    visibility: ExternalTaskVisibility.Private,
    allowedProcessKeys: ["billing", "refunds"]);
```

- **Deployment is where it's enforced.** A deployment that references a private task type from a process not on its allow-list is rejected with an error naming the process and the type. That covers direct deploys, the editor and deployment requests. A request is rejected when submitted, and if the process loses access before approval, the approved request records the deploy error and can be retried.
- **Only new deployments are affected.** Allow-list changes don't touch running instances, and versions already deployed can still start new instances. To stop those, [disable the process](/fleans/reference/api/).
- **Managing the allow-list.** Use **Edit** in the *Allowed processes* column of the *Custom Tasks* page, or [`PUT /custom-tasks/{taskType}/allowed-processes`](/fleans/reference/api/). The list the worker declares applies until the first edit. After that the stored list wins, and later worker restarts don't change it.
- **The editor hides what a process can't use.** A private task type appears in the picker, and in what the AI assistant is told about, only for an allowed process. A task that already uses a type its process can't use shows a warning.
- **The restriction outlives the worker.** When every worker of a private type is gone, the type stays on the *Custom Tasks* page with no workers, its allow-list stays editable, and deployments are still checked. The editor doesn't offer it until a worker returns.
- **Turning a type public again.** It becomes public once it has workers and all of them declare it public, and its allow-list is then discarded. While any connected worker still declares it private, it stays private. Nothing else removes the restriction.

Workers are trusted to declare their own visibility. A task type nobody has registered counts as public, so connect a worker declaring a private type before relying on its restriction.

## Ready-made plugins (fleans-plugins)

The engine images don't bundle any plugin. Ready-made plugins live in the separate
[fleans-plugins](https://github.com/nightBaker/fleans-plugins) repository, released in step
with Fleans — each as a NuGet package for your own worker, and all of them together in one
ready-to-run worker image, `ghcr.io/nightbaker/fleans-plugins`:

| Plugin | Task type | NuGet package | Guide |
|---|---|---|---|
| REST Caller — outbound HTTP call, response mapped back into the workflow | `rest-call` | `Fleans.Plugins.RestCaller` | [REST Caller guide](https://github.com/nightBaker/fleans-plugins/blob/main/docs/rest-caller/README.md) |
| Camunda 7 external tasks — see [below](#camunda-7-external-tasks) | `camunda:<topic>` | `Fleans.Plugins.CamundaExternalTask` | [Camunda guide](https://github.com/nightBaker/fleans-plugins/blob/main/docs/camunda-external-task/README.md) |

Run the image next to the engine with `Fleans__ExternalTaskWorker__EngineUrl` pointing at the API (see
[Hosting workers](/fleans/guides/custom-worker-host/)), or reference the package from
your own worker. Each plugin's parameters, failure codes and examples are in its guide.

**Authoring in the editor.** When you select a plugin on a `<bpmn:serviceTask>` in `/editor`, the properties panel renders typed input fields driven by the parameter schema the worker announced (so for REST Caller you see `URL`, `Method`, `Timeout`, …). **Output mappings stay free-form** — the schema doesn't enumerate possible outputs because results are dynamic (HTTP response bodies, SQL query shapes, etc.). Add them under *Outputs* in the panel's [Input/Output mapping](/fleans/guides/editor/#inputoutput-mapping) section, which also lists any input the schema doesn't declare. By convention, handlers expose their result under the reserved `__response` key (see [Mapping grammar](#mapping-grammar)), so output mappings read e.g. `source="=__response.body"`.

## Cancellation

Handlers receive a `CancellationToken` that fires when the engine stops waiting for the task (execution timeout, engine shutdown), the worker loses its connection, or the host stops. An `OperationCanceledException` from it is **not** a failure: the worker reports nothing and the task is delivered again. Thread the token through long-running I/O (`HttpClient.SendAsync(request, ct)`, `Task.Delay(timeout, ct)`).

A handler that ignores the token keeps running in the worker process. Its late report is dropped, but its side effects still happen, possibly next to a second run elsewhere. Another reason to key them by `ActivityInstanceId`.

An `OperationCanceledException` from any other token (a handler-internal timeout, for example) is a regular failure with code `500`. To tell them apart, link your per-request timeout with the supplied token using `CancellationTokenSource.CreateLinkedTokenSource`, and throw `ExternalTaskFailedException` for your own timeout (the REST Caller plugin is a worked example).

## Tasks that finish later

A handler can take a task and let something else report it: return `ExternalTaskHandler.Deferred` (`DeferTask` over the protocol). The engine stops waiting and the activity stays active, with no timeout, until a call to `POST /Execution/complete-activity` or `fail-activity` quotes the activity instance id. Record the task idempotently before returning, since a redelivery can run the handler again. A handler registered as `<scheme>:*` serves every `<scheme>:…` type no worker claims exactly. See [External Workers](/fleans/guides/external-workers/#tasks-that-finish-later).

## Camunda 7 external tasks

The Camunda external-task plugin from [fleans-plugins](https://github.com/nightBaker/fleans-plugins)
keeps Camunda 7 external-task workers running unchanged. It queues
`<serviceTask type="camunda:<topic>">` tasks and serves the Camunda `/engine-rest/external-task`
REST API. See [Migrating Camunda 7 External Tasks](/fleans/guides/camunda-external-tasks/).

## Upgrade note: plugin silos are gone

Earlier releases ran handlers in `Plugin`-role silos that joined the cluster. That role, `CustomTaskHandlerBase`, `AddCustomTaskPlugin<T>`, `AddFleansPluginHost`, the `Fleans:ExternalTasks:TaskTypes` setting and the `Fleans.Worker`, `Fleans.Application.Abstractions` and `Fleans.Domain.Abstractions` packages no longer exist. There is no compatibility layer: move handlers to `ExternalTaskHandler`, replace `CustomTaskFailedActivityException` with `ExternalTaskFailedException`, drop the `[ImplicitStreamSubscription]` attribute, and connect the host with `Fleans__ExternalTaskWorker__EngineUrl` instead of cluster settings.

## Reference

- [Issue #1013](https://github.com/nightBaker/fleans/issues/1013) — plugins as gRPC workers.
- [External Workers](/fleans/guides/external-workers/) — the protocol and delivery contract.
