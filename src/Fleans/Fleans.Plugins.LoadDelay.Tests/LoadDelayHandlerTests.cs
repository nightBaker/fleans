using System.Diagnostics;
using System.Dynamic;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Fleans.Plugins.LoadDelay.Tests;

[TestClass]
public class LoadDelayHandlerTests
{
    private static LoadDelayHandler MakeHandler() =>
        new(NullLogger<LoadDelayHandler>.Instance, Substitute.For<IGrainFactory>());

    private static CustomTaskExecutionContext Ctx() =>
        new(WorkflowInstanceId: Guid.NewGuid(),
            WorkflowId: "wf",
            ProcessDefinitionId: null,
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: "DelayTask",
            TaskType: LoadDelayHandler.LoadDelayTaskType);

    [TestMethod]
    public async Task ExecuteAsync_ReturnsCompletedMarker()
    {
        var handler = MakeHandler();
        var result = await handler.ExecuteForTest(new Dictionary<string, object?>(), new ExpandoObject(), Ctx());

        Assert.IsTrue(result.ContainsKey("completed"));
        Assert.AreEqual(true, result["completed"]);
    }

    [TestMethod]
    public async Task ExecuteAsync_HonoursMinimumDelay()
    {
        var handler = MakeHandler();
        var sw = Stopwatch.StartNew();
        await handler.ExecuteForTest(new Dictionary<string, object?>(), new ExpandoObject(), Ctx());
        sw.Stop();

        // 100ms floor — Stopwatch may report a few ms below the requested delay on Windows
        // due to timer granularity. Allow 90ms slack to avoid flake.
        Assert.IsTrue(sw.ElapsedMilliseconds >= 90,
            $"Expected delay >= 90ms; got {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public async Task ExecuteAsync_ObservesCancellationToken()
    {
        var handler = MakeHandler();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            handler.ExecuteForTest(new Dictionary<string, object?>(), new ExpandoObject(), Ctx(), cts.Token));
    }

    [TestMethod]
    public void TaskTypeMatchesImplicitStreamSubscriptionLiteral()
    {
        // Hard guard against the "Subscriber-side stream-id trap" — the literal on
        // [ImplicitStreamSubscription] MUST equal WorkflowEventStreams.GetExecuteCustomTaskNamespace(TaskType).
        // AddCustomTaskPlugin validates this at silo startup; this unit test is a faster
        // signal so a typo doesn't make it to deployment.
        //
        // ImplicitStreamSubscriptionAttribute hides its namespace string behind a predicate
        // accessor — read the original constructor argument via CustomAttributeData, same
        // pattern as CustomTaskServiceCollectionExtensions:62.
        var literals = typeof(LoadDelayHandler)
            .GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(Orleans.ImplicitStreamSubscriptionAttribute))
            .Select(a => a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null)
            .Where(s => s is not null)
            .Cast<string>()
            .ToList();

        var expected = $"events.ExecuteCustomTaskEvent.{LoadDelayHandler.LoadDelayTaskType}";
        Assert.IsTrue(literals.Contains(expected),
            $"LoadDelayHandler must declare [ImplicitStreamSubscription(\"{expected}\")]. Found: [{string.Join(", ", literals)}]");
    }
}

internal static class LoadDelayHandlerTestExtensions
{
    public static Task<IDictionary<string, object?>> ExecuteForTest(
        this LoadDelayHandler handler,
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken ct = default)
    {
        // Reflection-based test seam — mirrors the pattern from RestCallerHandlerTests.
        // ExecuteAsync is protected on CustomTaskHandlerBase, so we can't call it directly
        // from a sibling assembly without exposing a test-only API on the production handler.
        var method = typeof(LoadDelayHandler).GetMethod("ExecuteAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LoadDelayHandler.ExecuteAsync not found");
        return (Task<IDictionary<string, object?>>)method.Invoke(
            handler, new object[] { resolvedInputs, variables, context, ct })!;
    }
}
