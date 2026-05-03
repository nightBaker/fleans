# Fleans.Application.Abstractions

Leaf abstractions package for Fleans worker plugins. Contains the Orleans stream-namespace
constants that plugin handlers need to subscribe to engine-published events
(`ExecuteCustomTaskEvent`, etc.) without taking a transitive reference on
`Fleans.Application` / `Fleans.Domain`.

This package has zero `<ProjectReference>` dependencies — only `Microsoft.Orleans.Sdk`.

## Minimum consumer usage

```csharp
using Fleans.Application.Abstractions.Events;

[ImplicitStreamSubscription(WorkflowEventStreams.ExecuteCustomTaskStreamNamespace)]
public sealed class MyHandler : Grain, IGrainWithStringKey, IAsyncObserver<ExecuteCustomTaskEvent>
{
    // ...
}
```

> Plugin packages share the engine's `<VersionPrefix>` track — every Fleans release bumps
> every plugin's NuGet version even when the plugin source is bit-identical (same precedent
> as `Aspire.Hosting.*` / `Microsoft.Orleans.*`).

See [Fleans documentation](https://nightbaker.github.io/fleans/) for the full plugin guide.
