using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/10-signal-events/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class SignalEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task SignalCatch_ReceivesBroadcast_WorkflowResumes()
    {
        var xml = BpmnFixtureLoader.Load("10-signal-events", "signal-catch-throw.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.IsStarted && s.ActiveActivityIds.Contains("waitSignal"));

        var delivery = await ApiClient.SendSignalAsync("globalAlert");
        Assert.IsGreaterThanOrEqualTo(1, delivery.DeliveredCount,
            $"Signal should reach at least 1 subscriber. Delivered={delivery.DeliveredCount}.");

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        state.AssertCompletedActivities("afterSignal");
        state.AssertVariableEquals("signalReceived", "True");
    }

    [TestMethod]
    [Ignore("Known bug: boundary events on IntermediateCatchEvent do not register subscriptions; see docs/plans/2026-02-25-manual-test-results.md.")]
    public async Task SignalBoundary_InterruptsTimer_EmergencyPathTaken()
    {
        var xml = BpmnFixtureLoader.Load("10-signal-events", "signal-boundary.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.IsStarted && s.ActiveActivityIds.Contains("longWait"));

        await ApiClient.SendSignalAsync("emergencyStop");

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        state.AssertCompletedActivities("emergencyPath");
        state.AssertNotCompleted("normalPath");
        state.AssertVariableEquals("emergency", "True");
    }
}
