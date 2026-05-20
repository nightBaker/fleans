using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/04-parallel-gateway/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class ParallelGatewayTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ForkJoin_BothBranchesComplete_JoinWaitsForBoth()
    {
        var xml = BpmnFixtureLoader.Load("04-parallel-gateway", "fork-join.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("branchA", "branchB", "afterJoin", "end");
        Assert.IsEmpty(state.ActiveActivityIds);
    }
}
