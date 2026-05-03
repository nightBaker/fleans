# Fleans.Worker

Orleans worker-side primitives for Fleans plugin authors. Use this package when you want to
implement a custom BPMN service-task plugin: it provides `CustomTaskHandlerBase`, the
`[WorkerPlacement]` attribute, and the placement directors that route worker grains onto
silos with `Fleans:Role=Worker` (or `Combined`).

## Minimum consumer usage

```csharp
public sealed class MyHandler : CustomTaskHandlerBase
{
    public MyHandler(ILogger<MyHandler> logger, IGrainFactory factory) : base(logger, factory) { }

    protected override string TaskType => "my-task";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        // your work here
        return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["__response"] = new { ok = true }
        });
    }
}

public static class MyPluginRegistration
{
    public static IServiceCollection AddMyPlugin(this IServiceCollection services) =>
        services.AddCustomTaskPlugin<MyHandler>(taskType: "my-task", displayName: "My Task");
}
```

> Plugin packages share the engine's `<VersionPrefix>` track — every Fleans release bumps
> every plugin's NuGet version even when the plugin source is bit-identical (same precedent
> as `Aspire.Hosting.*` / `Microsoft.Orleans.*`).

See [Fleans documentation](https://nightbaker.github.io/fleans/concepts/custom-tasks/) for the
full plugin-authoring guide and the [`Fleans.CustomWorkerHost`](https://nightbaker.github.io/fleans/guides/custom-worker-host/)
worked example for hosting plugins outside the engine repo.
