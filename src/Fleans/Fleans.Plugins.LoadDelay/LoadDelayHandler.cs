using System.Dynamic;
using Fleans.Application.Abstractions.Events;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;

namespace Fleans.Plugins.LoadDelay;

/// <summary>
/// Backs <c>&lt;serviceTask type="load-delay-100ms"&gt;</c>. Awaits <c>Task.Delay(100, ct)</c>
/// then returns a marker output. Used by tests/manual/60-streaming-shard-throughput to
/// drive the load-test workload measuring #565's per-instance stream sharding.
///
/// 100ms is fast enough for high throughput, slow enough to keep activations live
/// concurrently across many in-flight workflow instances (which is precisely the
/// condition under which #565's per-WorkflowInstanceId sharding matters).
/// </summary>
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.load-delay-100ms")]
public sealed class LoadDelayHandler : CustomTaskHandlerBase
{
    public const string LoadDelayTaskType = "load-delay-100ms";
    private static readonly TimeSpan DelayDuration = TimeSpan.FromMilliseconds(100);

    public LoadDelayHandler(ILogger<LoadDelayHandler> logger, IGrainFactory grainFactory)
        : base(logger, grainFactory)
    {
    }

    protected override string TaskType => LoadDelayTaskType;

    protected override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        await Task.Delay(DelayDuration, cancellationToken);
        return new Dictionary<string, object?> { ["completed"] = true };
    }
}
