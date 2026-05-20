using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/17-signal-start-event/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class SignalStartEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task SignalStartEvent_CreatesInstanceFromBroadcast()
    {
        var xml = BpmnFixtureLoader.Load("17-signal-start-event", "signal-start-event.bpmn");
        await ApiClient.DeployAsync(xml);

        // Signal start events route newly-created instances through `WorkflowInstanceIds`
        // rather than `DeliveredCount` (the latter counts already-subscribed catch events).
        // So the assertion is "at least one of the two is non-empty".
        var d1 = await ApiClient.SendSignalAsync("orderSignal");
        Assert.IsNotNull(d1.WorkflowInstanceIds);
        Assert.HasCount(1, d1.WorkflowInstanceIds);

        var firstState = await ApiClient.WaitForCompletionAsync(d1.WorkflowInstanceIds[0]);
        firstState.AssertCompletedActivities("sigStart", "task1", "end");

        var d2 = await ApiClient.SendSignalAsync("orderSignal");
        Assert.IsNotNull(d2.WorkflowInstanceIds);
        Assert.HasCount(1, d2.WorkflowInstanceIds);
        Assert.AreNotEqual(d1.WorkflowInstanceIds[0], d2.WorkflowInstanceIds[0]);
    }

    [TestMethod]
    public async Task UnknownSignalName_Returns404()
    {
        var xml = BpmnFixtureLoader.Load("17-signal-start-event", "signal-start-event.bpmn");
        await ApiClient.DeployAsync(xml);

        using var response = await ApiClient.SendSignalRawAsync("unknownSignal");
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
