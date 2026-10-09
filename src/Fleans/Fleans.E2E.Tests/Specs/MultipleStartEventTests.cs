using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/24-multiple-event/test-plan.md (Scenario D — Multiple Start Event)
[TestClass]
[TestCategory("E2E")]
public class MultipleStartEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task MultipleStartEvent_EitherMessageOrSignal_CreatesAnInstance()
    {
        var xml = BpmnFixtureLoader.Load("24-multiple-event", "multiple-start.bpmn");
        await ApiClient.DeployAsync(xml);
        using (await ApiClient.EnableAsync("multiple-start-test")) { }

        // Trigger 1 — the message definition.
        var byMessage = await ApiClient.SendMessageAsync("multiStartOrder");
        Assert.IsTrue(byMessage.Delivered, "multiStartOrder should be delivered to the multiple start event.");
        Assert.IsNotNull(byMessage.WorkflowInstanceIds);
        Assert.HasCount(1, byMessage.WorkflowInstanceIds);
        var messageState = await ApiClient.WaitForCompletionAsync(byMessage.WorkflowInstanceIds[0]);
        messageState.AssertCompletedActivities("multiStart", "afterStart", "end");
        messageState.AssertVariableEquals("started", "True");

        // Trigger 2 — the signal definition of the same start event.
        var bySignal = await ApiClient.SendSignalAsync("multiStartOverride");
        Assert.IsNotNull(bySignal.WorkflowInstanceIds);
        Assert.HasCount(1, bySignal.WorkflowInstanceIds);
        Assert.AreNotEqual(byMessage.WorkflowInstanceIds[0], bySignal.WorkflowInstanceIds[0],
            "Each trigger must create its own instance.");
        var signalState = await ApiClient.WaitForCompletionAsync(bySignal.WorkflowInstanceIds[0]);
        signalState.AssertCompletedActivities("multiStart", "afterStart", "end");
        signalState.AssertVariableEquals("started", "True");
    }
}
