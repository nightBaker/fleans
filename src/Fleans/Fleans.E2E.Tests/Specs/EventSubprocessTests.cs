using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/19-23 — event sub-process variants
[TestClass]
[TestCategory("E2E")]
public class EventSubprocessTests : WorkflowE2ETestBase
{
    // tests/manual/19-event-subprocess-error/test-plan.md
    [TestMethod]
    public async Task ErrorEventSubprocess_CatchesUnhandledException_HandlerRuns()
    {
        var xml = BpmnFixtureLoader.Load("19-event-subprocess-error", "error-event-subprocess.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(15));

        state.AssertCompletedActivities("handlerTask", "handlerEnd");
        state.AssertNotCompleted("normalEnd");

        var failing = state.CompletedActivities.FirstOrDefault(a => a.ActivityId == "failingTask");
        Assert.IsNotNull(failing, "failingTask must appear among completed activities (with error state).");
        Assert.IsNotNull(failing.ErrorState, "failingTask must carry an ErrorState.");
    }

    // tests/manual/20-event-subprocess-timer/test-plan.md
    [TestMethod]
    public async Task TimerEventSubprocess_FiresAfterDelay_CancelsUserTask_HandlerRuns()
    {
        var xml = BpmnFixtureLoader.Load("20-event-subprocess-timer", "timer-event-subprocess.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(30));

        state.AssertCompletedActivities("handlerTask", "handlerEnd");
        state.AssertNotCompleted("normalEnd");
    }

    // tests/manual/21-event-subprocess-message/test-plan.md
    [TestMethod]
    public async Task MessageEventSubprocess_CorrelatedMessageInterruptsHost_HandlerRuns()
    {
        var xml = BpmnFixtureLoader.Load("21-event-subprocess-message", "message-event-subprocess.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?> { ["orderId"] = "ORD-123" });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("userTask"));

        var msg = await ApiClient.SendMessageAsync("cancelOrder", correlationKey: "ORD-123");
        Assert.IsTrue(msg.Delivered);

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(15));

        state.AssertCompletedActivities("handlerTask", "handlerEnd");
        state.AssertNotCompleted("normalEnd");
    }

    // tests/manual/22-event-subprocess-signal/test-plan.md
    [TestMethod]
    public async Task SignalEventSubprocess_BroadcastInterruptsMultipleInstances()
    {
        var xml = BpmnFixtureLoader.Load("22-event-subprocess-signal", "signal-event-subprocess.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);

        var first = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var second = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        await ApiClient.WaitForStateAsync(
            first.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("userTask"));
        await ApiClient.WaitForStateAsync(
            second.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("userTask"));

        var signal = await ApiClient.SendSignalAsync("cancelEverything");
        Assert.IsGreaterThanOrEqualTo(2, signal.DeliveredCount,
            $"Signal should fan-out to both running instances. Delivered={signal.DeliveredCount}.");

        var firstState = await ApiClient.WaitForCompletionAsync(first.WorkflowInstanceId);
        var secondState = await ApiClient.WaitForCompletionAsync(second.WorkflowInstanceId);

        foreach (var s in new[] { firstState, secondState })
        {
            s.AssertCompletedActivities("handlerTask");
            s.AssertNotCompleted("normalEnd");
        }
    }

    // tests/manual/23-event-subprocess-non-interrupting/test-plan.md
    // The full plan claims parentTask via UI completion to terminate normally — we don't
    // automate user-task UI claim yet, so this verifies only the parallel-handler step.
    [TestMethod]
    public async Task NonInterruptingTimerEventSubprocess_HandlerRunsParallelWithUserTask()
    {
        var xml = BpmnFixtureLoader.Load("23-event-subprocess-non-interrupting", "ni-event-subprocess.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.CompletedActivityIds.Contains("handlerEnd"),
            timeout: TimeSpan.FromSeconds(20));

        // Non-interrupting boundary: parentTask MUST stay active even after the handler ran.
        Assert.Contains("parentTask", state.ActiveActivityIds);
        state.AssertCompletedActivities("handlerTask", "handlerEnd");
    }
}
