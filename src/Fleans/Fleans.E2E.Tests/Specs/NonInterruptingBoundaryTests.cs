using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/15-non-interrupting-boundaries/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class NonInterruptingBoundaryTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task NonInterruptingTimer_FiresWithoutCancellingHost_BoundaryPathExecutes()
    {
        var xml = BpmnFixtureLoader.Load("15-non-interrupting-boundaries", "non-interrupting-timer.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        // Per the manual plan: longTask uses TaskActivity awaiting external completion;
        // it stays Active. The boundary timer should fire ~5s in and complete sendReminder.
        var state = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.CompletedActivityIds.Contains("reminderEnd"),
            timeout: TimeSpan.FromSeconds(20));

        Assert.Contains("longTask", state.ActiveActivityIds,
            "longTask must remain active (non-interrupting boundary).");
        state.AssertCompletedActivities("sendReminder", "reminderEnd");
        state.AssertVariableEquals("reminderSent", "True");
    }

    [TestMethod]
    public async Task NonInterruptingMessage_DeliveredWithoutCancellingHost_BoundaryPathExecutes()
    {
        var xml = BpmnFixtureLoader.Load("15-non-interrupting-boundaries", "non-interrupting-message.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?> { ["orderId"] = "order-123" });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("longTask"));

        var msg = await ApiClient.SendMessageAsync("reminderMessage", correlationKey: "order-123");
        Assert.IsTrue(msg.Delivered, "Boundary message should be delivered.");

        var state = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.CompletedActivityIds.Contains("handleMessage"),
            timeout: TimeSpan.FromSeconds(10));

        Assert.Contains("longTask", state.ActiveActivityIds,
            "longTask must remain active (non-interrupting boundary).");
        state.AssertVariableEquals("messageReceived", "True");
    }

    [TestMethod]
    public async Task TimerCycle_R3PT5S_FiresExactlyThreeTimes()
    {
        var xml = BpmnFixtureLoader.Load("15-non-interrupting-boundaries", "timer-cycle.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        // Cycle fires every 5s up to 3 times — wait ~25s for the third sendReminder.
        var state = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.CompletedActivityIds.Count(id => id == "sendReminder") >= 3,
            timeout: TimeSpan.FromSeconds(45));

        Assert.Contains("longTask", state.ActiveActivityIds,
            "longTask must remain active throughout all cycle fires.");
        Assert.IsGreaterThanOrEqualTo(
            3,
            state.CompletedActivityIds.Count(id => id == "sendReminder"),
            "Timer cycle should fire sendReminder at least 3 times.");
    }
}
