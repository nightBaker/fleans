using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Tests.CustomTasks.TestStubs;

/// <summary>Plugin that always throws, so the activity fails; counts invocations.</summary>
[GrainType("custom-task.test-throwing")]
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.test-throwing")]
public sealed partial class TestThrowingCustomTaskHandler : CustomTaskHandlerBase
{
    public static int InvocationCount;

    public TestThrowingCustomTaskHandler(
        ILogger<TestThrowingCustomTaskHandler> logger,
        IGrainFactory grainFactory) : base(logger, grainFactory)
    {
    }

    protected override string TaskType => "test-throwing";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref InvocationCount);
        throw new InvalidOperationException("test-throwing plugin always fails");
    }
}
