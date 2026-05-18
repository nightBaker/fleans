using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Tests.CustomTasks.TestStubs;

/// <summary>
/// Deliberately broken: declares a stream-subscription namespace that does NOT match
/// its <see cref="TaskType"/>. Used by <c>AddCustomTaskPlugin&lt;T&gt;</c> validation
/// tests to verify the loud-failure path on attribute drift.
/// </summary>
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.wrong-name")]
public sealed partial class BadAttributeCustomTaskHandler : CustomTaskHandlerBase
{
    public BadAttributeCustomTaskHandler(
        ILogger<BadAttributeCustomTaskHandler> logger,
        IGrainFactory grainFactory) : base(logger, grainFactory)
    {
    }

    protected override string TaskType => "right-name";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>());
}
