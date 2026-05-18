using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Tests.CustomTasks.TestStubs;

[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.test-no-op-a")]
public sealed partial class TestNoOpCustomTaskHandlerA : CustomTaskHandlerBase
{
    public static int InvocationCount;

    public TestNoOpCustomTaskHandlerA(
        ILogger<TestNoOpCustomTaskHandlerA> logger,
        IGrainFactory grainFactory) : base(logger, grainFactory)
    {
    }

    protected override string TaskType => "test-no-op-a";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref InvocationCount);
        return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>());
    }
}
