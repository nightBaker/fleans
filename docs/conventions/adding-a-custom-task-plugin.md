# Adding a custom-task plugin

A *custom task* is a `<bpmn:serviceTask type="…">` whose execution is supplied by a handler running in a **gRPC worker**: a plain process, outside the Orleans cluster, built on `Fleans.ExternalTasks.Sdk` (or any language's gRPC client, from `external_task_worker.proto`). Use this — **not** [Adding a new BPMN activity](./adding-a-bpmn-activity.md) — when the new behavior is plugin-shaped (REST call, email, custom external system). "Plugin" now only means a packaged handler, such as those in [fleans-plugins](https://github.com/nightBaker/fleans-plugins); there is no Plugin role (#1013).

## Steps

1. **Project setup.** Reference the `Fleans.ExternalTasks.Sdk` NuGet package (it has no Orleans dependency; CI guards that) and write a class deriving from `ExternalTaskHandler`. Override `ExecuteAsync(resolvedInputs, variables, context, cancellationToken)`. The handler is resolved from a fresh DI scope per task, so constructor injection works and no state survives between tasks. Keep shared clients in DI.

2. **Error contract.** Throw `ExternalTaskFailedException(string code, string message)` to fail with a BPMN error code an error boundary can catch; any other exception fails with code `500`.

   **Handlers must be idempotent.** Delivery is at-least-once: a task is run again when its worker disconnects or crashes, when it reports nothing within `Fleans:ExternalTasks:ExecutionTimeout` (5 min), when the dispatcher deactivates, or when the engine's `CompleteActivity` fails transiently after the handler succeeded. Key external side effects by `context.ActivityInstanceId`, which stays the same across deliveries. The dispatcher's pre-dispatch check (`GetVariablesIfActive`) skips an already finished activity but cannot stop those re-runs ([invariants](invariants.md#stream-redelivery-idempotency) guard 8).

   **Cancellation is not a failure.** The token fires when the engine stops waiting (timeout, deactivation), the worker's connection drops, or the host stops. An `OperationCanceledException` from it is not reported, and the task is delivered again. One from the handler's own CTS is a failure with code `500`. Honour the token (`HttpClient.SendAsync(request, ct)`, `Task.Delay(timeout, ct)`).

   **Keep `ExecuteAsync` under `ExecutionTimeout`**, and that below the stream delivery window (`Fleans:Streaming:MaxEventDeliveryTime`, 6 min): the delivery waits for the outcome inside the stream call, and Orleans skips an event not delivered within the window, without any error, leaving the activity active ([streaming.md](streaming.md#delivery-window-and-redis-trimming-850)). The same window bounds how long a task waits for a worker to connect.

   **Deferred tasks.** A handler whose outcome arrives later (a person, or a worker polling a queue) records the task idempotently and returns `ExternalTaskHandler.Deferred` (`DeferTask` over the protocol). The dispatcher stops waiting and the activity stays active with no timeout, until `POST /Execution/complete-activity` or `fail-activity` quotes its `ActivityInstanceId`. The Camunda bridge in fleans-plugins works this way. A redelivery can still run the handler again.

   **The engine does not throttle by type** (#815): each task has its own dispatcher activation, so a parallel multi-instance task over 2 000 items offers 2 000 tasks at once, spread over the connected workers and capped per process by `MaxConcurrentTasks` (default 32). A handler calling a rate-limited system limits itself, for example by failing with a code on 429 for a BPMN retry.

3. **Return shape.** `ExecuteAsync` returns an `IDictionary<string, object?>`, serialized as JSON. Output mappings (`<zeebe:output source="=__response.body" target="…"/>`) walk that dictionary.

4. **DI registration.** Expose an extension method next to the handler:

   ```csharp
   public static IServiceCollection AddMyThingPlugin(this IServiceCollection services) =>
       services.AddExternalTaskHandler<MyThingHandler>(taskType: "my-thing", displayName: "My Thing");
   ```

   A host calls `AddFleansExternalTaskWorker()` once and then these extensions; its only configuration is `Fleans:ExternalTaskWorker:EngineUrl`. `AddExternalTaskHandler<T>` throws at startup on a task type already registered in the process and on a registration the engine would refuse. `parameterSchema` (`ExternalTaskParameterSchema`) gives the editor typed inputs and reaches the catalog through `SubscribeRequest`.

   **Wildcards.** `taskType: "<scheme>:*"` claims every `<scheme>:…` type no worker claims exactly (exact wins). A wildcard is not listed in the catalog, since authors cannot pick it.

   **Category and access (#1022).** Optional `category`, `visibility` (`ExternalTaskVisibility`) and `allowedProcessKeys` arguments; a public task with allowed keys throws at startup, and the gRPC service rejects it with `CustomTaskAccessRules.ValidateRegistration`. See [Private custom tasks](#private-custom-tasks-1022).

5. **Tests.** Unit-test the handler by calling `ExecuteAsync` directly. In the engine repo, `GrpcExternalTaskWorkerTests` runs handlers against a TestCluster over the real protocol; cover completion **and** failure.

6. **Documentation.** Document the parameter schema and limitations next to the plugin (the engine repo bundles none; ready-made ones live in [fleans-plugins](https://github.com/nightBaker/fleans-plugins), one guide per plugin under `docs/<plugin>/`). If it ships from fleans-plugins, add a row to the *Ready-made plugins* table in `website/src/content/docs/concepts/custom-tasks.md`.

## Private custom tasks (#1022)

- **Where the rule lives:** `CustomTaskCatalogState.PrivateAccess`, one row per private task type (table `CustomTaskPrivateAccess`), not the `(TaskType, SiloName)` rows. Those rows are removed when a worker leaves, and a restriction must not end with the worker.
- **Private or public:** a task type is private while it has an access row. A registration declaring private creates the row. The row is dropped only once the type has workers and all of them declare public (`DropAccessOfPublicTasks`), so an offline task type stays restricted and a misconfigured public worker can't open up a type another worker keeps private. There is no way to remove the row of a task type that never comes back.
- **Allow-list ownership:** the declared list follows the workers until `SetAllowedProcesses` sets `AllowListManaged`; from then on the stored list wins.
- **Enforcement:** `CustomTaskAccessValidator.EnsureAllowed`, called from `ProcessDefinitionGrain.DeployVersion` (every deploy path ends there) and `DeploymentRequestGrain.Submit` (so reviewers never see a request that can't deploy). It fails closed: if the catalog can't be reached, the deploy fails. Processes with no custom tasks don't call the catalog. It is never checked at instance start, so running instances and deployed versions are unaffected.
- **A new activity that nests activities** must be reachable from `CustomTaskAccessValidator.CollectFromActivity` (today: `IWorkflowDefinition` scopes and `MultiInstanceActivity.InnerActivity`), or its tasks skip the check.

## Worker hosts

A host is a plain .NET generic host. The template repo <https://github.com/nightBaker/fleans-custom-worker-example> is the starting point: click *Use this template*, register handlers, set `Fleans__ExternalTaskWorker__EngineUrl`, ship the image. It joins no cluster, so no clustering, streaming or reminder settings apply.
