using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Tests.CustomTasks.TestStubs;

/// <summary>
/// Records every execution (activity instance → silo) and holds it open until the test
/// releases <see cref="Gate"/>, so a test can observe many executions in flight at once.
/// </summary>
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.test-gated")]
public sealed partial class TestGatedCustomTaskHandler : CustomTaskHandlerBase
{
    public static readonly ConcurrentQueue<(Guid ActivityInstanceId, string SiloName)> Executions = new();
    public static TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _inFlight;
    private static int _maxInFlight;

    public static int MaxInFlight => Volatile.Read(ref _maxInFlight);

    private readonly string _siloName;

    public TestGatedCustomTaskHandler(
        ILogger<TestGatedCustomTaskHandler> logger,
        IGrainFactory grainFactory,
        ILocalSiloDetails siloDetails) : base(logger, grainFactory)
    {
        _siloName = siloDetails.Name;
    }

    public static void Reset()
    {
        Executions.Clear();
        Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _inFlight, 0);
        Volatile.Write(ref _maxInFlight, 0);
    }

    protected override string TaskType => "test-gated";

    protected override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        Executions.Enqueue((context.ActivityInstanceId, _siloName));
        var now = Interlocked.Increment(ref _inFlight);
        int max;
        while (now > (max = Volatile.Read(ref _maxInFlight)) &&
               Interlocked.CompareExchange(ref _maxInFlight, now, max) != max) { }

        try
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        return new Dictionary<string, object?>();
    }
}
