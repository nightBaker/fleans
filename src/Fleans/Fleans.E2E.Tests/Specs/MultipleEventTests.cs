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

    // `longTask` is a user task, so it stays active until the boundary interrupts it —
    // the message has a deterministic window (the boundary's own timer is PT10S).
    [TestMethod]
    public async Task MultipleBoundary_MessageFiresFirst_EscalationPathTaken()
    {
        var xml = BpmnFixtureLoader.Load("24-multiple-event", "multiple-boundary.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("longTask"));

        var msg = await ApiClient.SendMessageAsync("cancelOrder", correlationKey: "order-boundary-1");
        Assert.IsTrue(msg.Delivered, "cancelOrder should be delivered to the multiple boundary.");

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        state.AssertCompletedActivities("escalation", "escalationEnd");
        state.AssertCancelled("longTask");
        state.AssertNotCompleted("normalEnd");
        state.AssertVariableEquals("escalated", "True");
    }

    [TestMethod]
    public async Task MultipleBoundary_TimerFiresWhenNoMessage_EscalationPathTaken()
    {
        var xml = BpmnFixtureLoader.Load("24-multiple-event", "multiple-boundary.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("longTask"));

        // No message: the PT10S timer branch of the boundary must interrupt the user task.
        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(45));
        state.AssertCompletedActivities("escalation", "escalationEnd");
        state.AssertCancelled("longTask");
        state.AssertNotCompleted("normalEnd");
    }
}
