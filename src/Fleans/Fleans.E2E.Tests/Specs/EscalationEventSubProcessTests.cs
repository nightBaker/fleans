using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/24-escalation-event/test-plan.md (Test D — escalation caught by an Event Sub-Process)
[TestClass]
[TestCategory("E2E")]
public class EscalationEventSubProcessTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task EscalationThrownInSubProcess_CaughtByNonInterruptingEventSubProcess_BothPathsComplete()
    {
        var xml = BpmnFixtureLoader.Load("24-escalation-event", "escalation-event-subprocess.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("throwEscalation", "escalationHandler", "afterThrow", "work", "end");
        state.AssertVariableEquals("mainContinued", "True");
        // A non-interrupting event sub-process runs on a copy of the scope and never writes back.
        Assert.IsFalse(state.VariableStates.Any(v => v.Variables.ContainsKey("escalationHandled")),
            "The handler's writes stay in its own scope.");
    }
}
