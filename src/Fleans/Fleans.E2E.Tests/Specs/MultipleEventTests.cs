using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/24-multiple-event/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class MultipleEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task MultipleIntermediateCatch_MessageWins_OtherSubscriptionCancelled()
    {
        var xml = BpmnFixtureLoader.Load("24-multiple-event", "message-or-signal-catch.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?> { ["orderId"] = "order-multi-1" });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("multiCatch"));

        var msg = await ApiClient.SendMessageAsync(
            "paymentReceived",
            correlationKey: "order-multi-1",
            variables: new Dictionary<string, object?> { ["amount"] = 99 });
        Assert.IsTrue(msg.Delivered);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        state.AssertCompletedActivities("multiCatch", "afterCatch", "end");
    }

    [TestMethod]
    public async Task MultipleIntermediateCatch_SignalWins_OtherSubscriptionCancelled()
    {
        var xml = BpmnFixtureLoader.Load("24-multiple-event", "message-or-signal-catch.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?> { ["orderId"] = "order-multi-2" });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("multiCatch"));

        var signal = await ApiClient.SendSignalAsync("manualOverride");
        Assert.IsGreaterThanOrEqualTo(1, signal.DeliveredCount);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        state.AssertCompletedActivities("multiCatch", "afterCatch", "end");
    }

    [TestMethod]
    public async Task MultipleIntermediateThrow_FiresTwoSignals_WorkflowCompletes()
    {
        var xml = BpmnFixtureLoader.Load("24-multiple-event", "multi-throw.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        Assert.IsTrue(state.IsCompleted);
    }

    // TODO: timing-sensitive — `longTask` completes very quickly and the cancel message
    // doesn't reach the boundary before normalEnd fires. Requires a fixture with a longer
    // host activity, or an active-state wait that the engine guarantees (the catch
    // events become active before script tasks complete, but TaskActivity isn't a
    // catch event so we can't WaitForState on its subscription).
    [TestMethod]
    [Ignore("Timing-sensitive: longTask completes before the cancel message arrives in test cluster.")]
    public async Task MultipleBoundary_MessageFiresFirst_EscalationPathTaken()
    {
        var xml = BpmnFixtureLoader.Load("24-multiple-event", "multiple-boundary.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?> { ["orderId"] = "order-boundary-1" });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("longTask"));

        await ApiClient.SendMessageAsync("cancelOrder", correlationKey: "order-boundary-1");

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        state.AssertCompletedActivities("escalation", "escalationEnd");
    }
}
