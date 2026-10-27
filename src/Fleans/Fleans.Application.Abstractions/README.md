# Fleans.Application.Abstractions

Abstractions package for Fleans worker plugins. Contains the Orleans grain interfaces that
span Core ↔ Worker silos (`IScriptExecutorGrain`, `IConditionExpressionEvaluatorGrain`,
`ICustomTaskCatalogGrain`, narrow `IWorkflowInstanceCallback`, and the transient
`WorkflowInstanceStaleException` its calls can throw — retry it), the custom-task schema
records (`CustomTaskParameterSchema`, `CustomTaskRegistration`, `CustomTaskCatalogEntry`),
`WorkflowLoggingContext` / `WorkflowContextKeys`, the stream-namespace
constants (`WorkflowEventStreams`), and the `Fleans` meter and activity source
(`FleansDiagnostics`).

Depends only on `Fleans.Domain.Abstractions` (the true leaf) + `Microsoft.Orleans.Sdk` +
`Microsoft.Extensions.Logging.Abstractions`. No reference to `Fleans.Application`,
`Fleans.Domain`, or any persistence project.

## Dependency closure

```
Fleans.Application.Abstractions  →  Fleans.Domain.Abstractions
```

Used by:
- **`Fleans.Worker`** — depends on this package, which transitively pulls Domain.Abstractions.
- **`Fleans.Application`** — internal engine implementation, depends on this package for the
  shared grain interfaces it implements.

## Minimum consumer usage

Plugin handlers derive from `CustomTaskHandlerBase` in **`Fleans.Worker`** (see its README) and
subscribe to their own per-task-type namespace, built from the constants here:

```csharp
// WorkflowEventStreams.GetExecuteCustomTaskNamespace("my-task") == "events.ExecuteCustomTaskEvent.my-task"
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.my-task")]
public sealed class MyHandler : CustomTaskHandlerBase { /* ... */ }
```

> Plugin packages share the engine's `<VersionPrefix>` track — every Fleans release bumps
> every plugin's NuGet version even when the plugin source is bit-identical (same precedent
> as `Aspire.Hosting.*` / `Microsoft.Orleans.*`).

See [Fleans documentation](https://nightbaker.github.io/fleans/) for the full plugin guide.
