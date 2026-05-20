using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/14-inclusive-gateway/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class InclusiveGatewayTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ParallelConditions_TwoBranchesTrue_JoinWaitsForBoth()
    {
        var xml = BpmnFixtureLoader.Load("14-inclusive-gateway", "parallel-conditions.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities(
            "start", "setup", "fork", "branch1", "branch2", "join", "afterJoin", "end");
        state.AssertNotCompleted("branch3");
        state.AssertVariableEquals("path1", "taken");
        state.AssertVariableEquals("path2", "taken");
        state.AssertVariableEquals("joined", "True");
        Assert.IsFalse(state.TryGetVariable("path3", out _),
            "path3 should never be set since branch3's condition is false.");
    }

    [TestMethod]
    public async Task DefaultFlow_AllConditionsFalse_DefaultPathTaken()
    {
        var xml = BpmnFixtureLoader.Load("14-inclusive-gateway", "default-flow.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("start", "setup", "fork", "defaultTask", "defaultEnd");
        state.AssertNotCompleted("highTask", "lowTask");
        state.AssertVariableEquals("path", "default");
    }

    [TestMethod]
    public async Task NestedInclusive_BothLevelsForkAndJoinCorrectly()
    {
        var xml = BpmnFixtureLoader.Load("14-inclusive-gateway", "nested-inclusive.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities(
            "start", "setup", "outerFork",
            "innerFork", "branchA1", "branchA2", "innerJoin", "afterInnerJoin",
            "branchB", "outerJoin", "end");
    }
}
