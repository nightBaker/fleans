using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Tests.CustomTasks.TestStubs;

[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.test-no-op-b")]
public sealed partial class TestNoOpCustomTaskHandlerB : CustomTaskHandlerBase
{
    public static int InvocationCount;

    public TestNoOpCustomTaskHandlerB(
        ILogger<TestNoOpCustomTaskHandlerB> logger,
        IGrainFactory grainFactory) : base(logger, grainFactory)
    {
    }

    protected override string TaskType => "test-no-op-b";

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
