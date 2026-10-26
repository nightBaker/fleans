using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Tests.CustomTasks.TestStubs;

/// <summary>
/// Deliberately broken: a matching stream subscription but no <c>[GrainType]</c>, so its grain
/// type would follow the class name. Used by <c>AddCustomTaskPlugin&lt;T&gt;</c> validation tests.
/// </summary>
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.no-grain-type")]
public sealed partial class NoGrainTypeCustomTaskHandler : CustomTaskHandlerBase
{
    public NoGrainTypeCustomTaskHandler(
        ILogger<NoGrainTypeCustomTaskHandler> logger,
        IGrainFactory grainFactory) : base(logger, grainFactory)
    {
    }

    protected override string TaskType => "no-grain-type";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>());
}

/// <summary>
/// Deliberately broken: a matching stream subscription but a <c>[GrainType]</c> that does not
/// follow its <see cref="TaskType"/>. Used by <c>AddCustomTaskPlugin&lt;T&gt;</c> validation tests.
/// </summary>
[GrainType("custom-task.someone-else")]
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.drifted-grain-type")]
public sealed partial class DriftedGrainTypeCustomTaskHandler : CustomTaskHandlerBase
{
    public DriftedGrainTypeCustomTaskHandler(
        ILogger<DriftedGrainTypeCustomTaskHandler> logger,
        IGrainFactory grainFactory) : base(logger, grainFactory)
    {
    }

    protected override string TaskType => "drifted-grain-type";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>());
}
