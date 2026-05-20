using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/03-exclusive-gateway/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class ExclusiveGatewayTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ConditionalBranching_HighPathTaken_LowPathNotTaken()
    {
        var xml = BpmnFixtureLoader.Load("03-exclusive-gateway", "conditional-branching.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("start", "setX", "highTask", "highEnd");
        state.AssertNotCompleted("lowTask", "lowEnd");
        state.AssertVariableEquals("x", "7");
    }
}
