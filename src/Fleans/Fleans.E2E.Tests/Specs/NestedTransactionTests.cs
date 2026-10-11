using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/53-nested-transaction/test-plan.md
//
// Scenario A (happy path) + Scenario B (inner cancels) are automated. Scenarios C and F
// require additional setup or cross-reference plan #26's hazard fixture — out of scope
// for this batch.
//
// The outer catch event's message correlates on `txKey`: intermediate message catch events
// require a correlation key (uncorrelated messages only reach message start events). Each
// run uses a fresh key so concurrent runs against the same stack don't cross-deliver.
[TestClass]
[TestCategory("E2E")]
public class NestedTransactionTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ScenarioA_BothTransactionsCommit_HappyPath()
    {
        var txKey = $"nested-tx-a-{Guid.NewGuid():N}";
        var xml = BpmnFixtureLoader.Load("53-nested-transaction", "nested-tx-normal-inner.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            new Dictionary<string, object?> { ["txKey"] = txKey });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("trigger-outer-complete-catch"),
            timeout: TimeSpan.FromSeconds(30));

        var msg = await ApiClient.SendMessageAsync("trigger-outer-complete", correlationKey: txKey);
        Assert.IsTrue(msg.Delivered);

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(30));

        state.AssertCompletedActivities(
            "inner-work", "inner-end", "inner-tx",
            "trigger-outer-complete-catch", "outer-end", "outer-tx", "process-end");
        state.AssertVariableEquals("innerDone", "True");
    }

    [TestMethod]
    public async Task ScenarioB_InnerCancelsAndCompensates_OuterCommits()
    {
        var txKey = $"nested-tx-b-{Guid.NewGuid():N}";
        var xml = BpmnFixtureLoader.Load("53-nested-transaction", "nested-tx-cancel-inner.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            new Dictionary<string, object?> { ["txKey"] = txKey });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("trigger-outer-complete-catch"),
            timeout: TimeSpan.FromSeconds(30));

        var msg = await ApiClient.SendMessageAsync("trigger-outer-complete", correlationKey: txKey);
        Assert.IsTrue(msg.Delivered);

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(30));

        state.AssertCompletedActivities(
            "inner-work", "inner-cancel-end", "inner-compensate", "inner-tx",
            "trigger-outer-complete-catch", "outer-end", "outer-tx", "process-end");
        state.AssertVariableEquals("innerDone", "True");
        state.AssertVariableEquals("innerCompensated", "True");
    }
}
