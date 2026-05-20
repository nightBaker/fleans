using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/07-subprocess/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class SubprocessTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task EmbeddedSubprocess_AllParentAndChildActivitiesComplete()
    {
        var xml = BpmnFixtureLoader.Load("07-subprocess", "embedded-subprocess.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities(
            "start", "sub1", "afterSub", "end",
            "subStart", "subScript", "subEnd");
        state.AssertVariableEquals("subVar", "from-subprocess");
    }
}
