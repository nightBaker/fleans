using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/24-escalation-event/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class EscalationEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task EscalationEndEvent_WithInterruptingBoundary_HandlerCatchesEscalation()
    {
        // Deploy child first
        var childXml = BpmnFixtureLoader.Load("24-escalation-event", "child-escalation-end.bpmn");
        await ApiClient.DeployAsync(childXml);

        var parentXml = BpmnFixtureLoader.Load("24-escalation-event", "parent-escalation-interrupting.bpmn");
        var parent = await ApiClient.DeployAsync(parentXml);
        var started = await ApiClient.StartAsync(parent.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(20));

        state.AssertCompletedActivities("escalationHandler", "escalationEnd");
        state.AssertNotCompleted("happyEnd");
    }

    [TestMethod]
    public async Task EscalationThrow_WithNonInterruptingBoundary_BothPathsComplete()
    {
        var childXml = BpmnFixtureLoader.Load("24-escalation-event", "child-escalation-throw.bpmn");
        await ApiClient.DeployAsync(childXml);

        var parentXml = BpmnFixtureLoader.Load("24-escalation-event", "parent-escalation-non-interrupting.bpmn");
        var parent = await ApiClient.DeployAsync(parentXml);
        var started = await ApiClient.StartAsync(parent.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(20));

        // Non-interrupting: both the handler path AND the happy path should complete.
        state.AssertCompletedActivities("escalationHandler", "escalationEnd", "happyEnd");
    }
}
