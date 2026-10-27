using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Tests.CustomTasks.TestStubs;

/// <summary>
/// Returns <c>{ received: &lt;the resolved inputs&gt;, status: "done" }</c> and records the last
/// inputs it saw, so io-mapping tests can check what reached the plugin and what left it.
/// </summary>
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.test-echo")]
public sealed partial class TestEchoCustomTaskHandler : CustomTaskHandlerBase
{
    public static IDictionary<string, object?>? LastInputs;

    public TestEchoCustomTaskHandler(
        ILogger<TestEchoCustomTaskHandler> logger,
        IGrainFactory grainFactory) : base(logger, grainFactory)
    {
    }

    protected override string TaskType => "test-echo";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        LastInputs = resolvedInputs;
        var received = new ExpandoObject();
        foreach (var (key, value) in resolvedInputs)
            ((IDictionary<string, object?>)received)[key] = value;
        return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["received"] = received,
            ["status"] = "done"
        });
    }
}
