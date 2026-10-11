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
    [Ignore("Blocked by #783: escalation-triggered event sub-processes are not supported (start event parsed as plain StartEvent).")]
    public async Task EscalationThrownInSubProcess_CaughtByNonInterruptingEventSubProcess_BothPathsComplete()
    {
        var xml = BpmnFixtureLoader.Load("24-escalation-event", "escalation-event-subprocess.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("throwEscalation", "escalationHandler", "afterThrow", "work", "end");
        state.AssertVariableEquals("escalationHandled", "True");
        state.AssertVariableEquals("mainContinued", "True");
    }
}
