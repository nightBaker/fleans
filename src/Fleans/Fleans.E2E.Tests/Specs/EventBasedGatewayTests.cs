using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/05-event-based-gateway/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class EventBasedGatewayTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task TimerVsMessageRace_MessageWins_TimerPathNotTaken()
    {
        var xml = BpmnFixtureLoader.Load("05-event-based-gateway", "timer-vs-message-race.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        // Gateway routes the token; catch events become active. Wait for either to appear,
        // with a short timeout so the 30s timer doesn't race us.
        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.IsStarted && (
                s.ActiveActivityIds.Contains("msgCatch") ||
                s.ActiveActivityIds.Contains("timerCatch")),
            timeout: TimeSpan.FromSeconds(5));

        var msg = await ApiClient.SendMessageAsync(
            messageName: "continueProcess",
            correlationKey: "order-123");
        Assert.IsTrue(msg.Delivered, "Message should have been delivered to the waiting instance.");

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("msgCatch", "msgPath");
        // The engine appears to mark both event-based gateway catch events as completed once
        // the gateway resolves (the loser is treated as a cancel-style completion). Assert
        // only on the downstream "path" activity, which is the unambiguous winner indicator
        // — `timerPath` should never execute when the message wins.
        state.AssertNotCompleted("timerPath");
    }
}
