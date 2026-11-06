# Worker NuGet stack

Worker authors consume one package: **`Fleans.ExternalTasks.Sdk`** (#1013). It carries `ExternalTaskHandler`, `ExternalTaskFailedException`, `ExternalTaskParameterSchema`, `ExternalTaskVisibility`, `AddFleansExternalTaskWorker` and `AddExternalTaskHandler<T>`.

## The SDK has no Orleans dependency

The engine's `Fleans.Domain.Abstractions` and `Fleans.Application.Abstractions` depend on `Microsoft.Orleans.Sdk`, so the SDK references neither. `Protos/external_task_worker.proto` (in the SDK project) is the only contract: the SDK generates an internal client, `Fleans.Application` generates the server from the same file. The SDK's parameter and visibility types are its own; the gRPC messages translate between them and the engine's catalog records. CI fails if `dotnet list Fleans.ExternalTasks.Sdk package --include-transitive` shows an Orleans package. **Do not add a project reference to the SDK.**

## Internal projects

`Fleans.Domain.Abstractions`, `Fleans.Application.Abstractions` and `Fleans.Worker` stay as engine projects, with `IsPackable=false`: they are no longer published. Nothing outside this repo builds against grain interfaces or stream events. Their split still keeps the worker silo's script and condition grains (`Fleans.Worker`) out of `Fleans.Application`.

- **`Fleans.Domain.Abstractions`** — `IDomainEvent`, event records carried on streams (`ExecuteCustomTaskEvent`, `InstanceAdvanced`), `InputMapping` / `OutputMapping`, the exception hierarchy (`ActivityException`, `CustomTaskFailedActivityException`), the `ExpandoObject` surrogate.
- **`Fleans.Application.Abstractions`** — grain interfaces spanning Core and Worker silos, catalog records (`CustomTaskParameterSchema`, `CustomTaskCatalogEntry`), `WorkflowEventStreams`, placement types.
- **`Fleans.Worker`** — `ScriptExecutorGrain`, `ConditionExpressionEvaluatorGrain`, placement directors, `AddFleansPlacementAssertion`. Referenced by `Fleans.Api` and `Fleans.WorkerHost`.

## Refactor convention — namespace-preserving moves

When relocating types into an abstractions project, **preserve the source namespace** (precedent: `Microsoft.Extensions.Logging.Abstractions` puts types in `Microsoft.Extensions.Logging`). It means no `using` churn, and `[GenerateSerializer]` FQNs and `[Alias("…")]` declarations stay unchanged.

## Reflection-by-assembly trap

Code that scans `typeof(IDomainEvent).Assembly.GetTypes()` (or any other anchor type in `*.Abstractions`) sees only the few types in `Fleans.Domain.Abstractions.dll`, not the dozens in `Fleans.Domain.dll`. `JournalEventSerializationTests` anchors its scan on `typeof(WorkflowEvent).Assembly` for that reason. **New reflective scans over a marker interface in an abstractions project** must scan both the abstractions assembly and the concrete-types assembly.

## Versioning

`Fleans.ExternalTasks.Sdk` shares the engine's `<VersionPrefix>` track (`src/Fleans/Directory.Build.props`). Ready-made plugin packages (`Fleans.Plugins.RestCaller`, `Fleans.Plugins.CamundaExternalTask`) and the `ghcr.io/nightbaker/fleans-plugins` worker image are released from [nightBaker/fleans-plugins](https://github.com/nightBaker/fleans-plugins) on the same track, after the engine release. Workers talk to the engine over the versioned protocol (`fleans.externaltasks.v1`), so they do not have to be rebuilt with every engine release unless the protocol changes.
