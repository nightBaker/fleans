# Plugin-author NuGet stack (leaf-package design)

Three NuGet packages compose the plugin-author surface. The dependency closure is strictly hierarchical so plugin authors get only what they need.

## Package layout

- **`Fleans.Domain.Abstractions`** is the true leaf — depends only on `Microsoft.Orleans.Sdk`. Holds:
  - the marker interface (`IDomainEvent`),
  - event records carried on Orleans streams (`ExecuteCustomTaskEvent`),
  - the value records those events reference (`InputMapping`, `OutputMapping`),
  - the exception hierarchy plugins may throw (`ActivityException`, `CustomTaskFailedActivityException`, `BadRequestActivityException`, `ActivityErrorState`),
  - the `ExpandoObject` Orleans surrogate (`Fleans.Domain.Surrogates.ExpandoObjectSurrogateConverter` + `ExpandoObjectSurrogate`) so plugin hosts get codec coverage for `ExpandoObject`-typed `IWorkflowInstanceCallback` parameters without referencing engine-internal projects.

- **`Fleans.Application.Abstractions`** depends on `Fleans.Domain.Abstractions` (NOT `Fleans.Domain`). Holds:
  - grain interfaces that span Core↔Worker silos (`IConditionExpressionEvaluatorGrain`, `IScriptExecutorGrain`, `ICustomTaskCatalogGrain`, narrow `IWorkflowInstanceCallback`),
  - custom-task schema records (`CustomTaskParameterSchema`, `CustomTaskRegistration`, `CustomTaskCatalogEntry`),
  - `MappingResolver`, `WorkflowLoggingContext`/`WorkflowContextKeys`, and stream-namespace constants (`WorkflowEventStreams`).

- **`Fleans.Worker`** depends only on `Fleans.Application.Abstractions` (transitively pulls Domain.Abstractions). Carries `CustomTaskHandlerBase`, `[WorkerPlacement]`, placement directors, and the worker-side grain implementations.

  **No reference to `Fleans.Application`, `Fleans.Domain`, `Fleans.Infrastructure`, or persistence projects.**

## What stays in `Fleans.Application` (not in Abstractions)

- The full `IWorkflowInstanceGrain` interface (uses heavy `Fleans.Domain.States.*` types beyond what Worker needs — Worker calls back via the narrow `IWorkflowInstanceCallback` that `IWorkflowInstanceGrain` extends).
- `WorkflowLoggingScopeFilter` (an `IIncomingGrainCallFilter` that references `IWorkflowInstanceGrain` for its skip-self check).

## Refactor convention — namespace-preserving moves

When relocating types into an abstractions package, **preserve the source namespace**. Package name ≠ namespace: the namespace is the API contract; the package is the shipping unit.

**Precedent:** `Microsoft.Extensions.Logging.Abstractions` places types in the `Microsoft.Extensions.Logging` namespace.

**Why it matters:**

- Zero `using`-directive churn across callers.
- Orleans `[GenerateSerializer]` wire format stays bit-identical (FQN unchanged).
- `[Alias("…")]` declarations carry through unchanged.

The cycle/leak invariants are guarded post-refactor. If you find yourself adding a `<ProjectReference Include="..\Fleans.Domain\…" />` to `Fleans.Application.Abstractions.csproj`, or `<ProjectReference Include="..\Fleans.Application\…" />` to `Fleans.Worker.csproj`, that means a type should have moved instead — extend the abstractions package and revisit.

**Worked example:** issue #628 moved `Fleans.Domain.Surrogates.ExpandoObjectSurrogateConverter` (+ `ExpandoObjectSurrogate`) from `Fleans.Domain` to `Fleans.Domain.Abstractions` so external plugin hosts gain the `ExpandoObject` codec without referencing engine-internal projects. Namespace `Fleans.Domain.Surrogates` was preserved, the codec FQN is unchanged, and zero call sites needed editing.

## Reflection-by-assembly trap

Code that scans `typeof(IDomainEvent).Assembly.GetTypes()` (or any other anchor type in `*.Abstractions`) now sees only the few types that live in `Fleans.Domain.Abstractions.dll`, not the dozens that stayed in `Fleans.Domain.dll`.

`Fleans.Persistence.Events.EventTypeRegistry` was the one caller affected and now scans both assemblies, anchoring `Fleans.Domain` via `typeof(WorkflowStarted).Assembly`.

**When adding new reflective scans over `IDomainEvent` implementations** (or any other marker interface that lives in an abstractions package), scan **both** the abstractions assembly and the concrete-types assembly, anchored by a stable type in each.

## Plugin NuGet versioning

Plugin NuGet packages (`Fleans.Domain.Abstractions`, `Fleans.Application.Abstractions`, `Fleans.Worker`, `Fleans.Plugins.RestCaller`) share the engine's `<VersionPrefix>` track (from `src/Fleans/Directory.Build.props`) — every release bumps every plugin even if its source is bit-identical.
