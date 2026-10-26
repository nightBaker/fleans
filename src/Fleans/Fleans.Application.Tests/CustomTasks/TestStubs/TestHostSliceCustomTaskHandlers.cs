using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Tests.CustomTasks.TestStubs;

/// <summary>
/// Two plugins meant to live on two different plugin hosts: each records the silo it executed on,
/// so a test can check that neither one was placed on a silo that does not host it.
/// </summary>
public static class TestHostSliceExecutions
{
    public static readonly ConcurrentQueue<(string TaskType, string SiloName)> Executions = new();
}

[GrainType("custom-task.test-host-a")]
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.test-host-a")]
public sealed partial class TestHostACustomTaskHandler : CustomTaskHandlerBase
{
    private readonly string _siloName;

    public TestHostACustomTaskHandler(
        ILogger<TestHostACustomTaskHandler> logger,
        IGrainFactory grainFactory,
        ILocalSiloDetails siloDetails) : base(logger, grainFactory)
    {
        _siloName = siloDetails.Name;
    }

    protected override string TaskType => "test-host-a";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        TestHostSliceExecutions.Executions.Enqueue((TaskType, _siloName));
        return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>());
    }
}

[GrainType("custom-task.test-host-b")]
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.test-host-b")]
public sealed partial class TestHostBCustomTaskHandler : CustomTaskHandlerBase
{
    private readonly string _siloName;

    public TestHostBCustomTaskHandler(
        ILogger<TestHostBCustomTaskHandler> logger,
        IGrainFactory grainFactory,
        ILocalSiloDetails siloDetails) : base(logger, grainFactory)
    {
        _siloName = siloDetails.Name;
    }

    protected override string TaskType => "test-host-b";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        TestHostSliceExecutions.Executions.Enqueue((TaskType, _siloName));
        return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>());
    }
}
