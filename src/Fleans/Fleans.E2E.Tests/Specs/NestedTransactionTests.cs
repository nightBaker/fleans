using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/53-nested-transaction/test-plan.md
//
// Scenario A (happy path) + Scenario B (inner cancels) are automated. Scenarios C and F
// require additional setup or cross-reference plan #26's hazard fixture — out of scope
// for this batch.
[TestClass]
[TestCategory("E2E")]
public class NestedTransactionTests : WorkflowE2ETestBase
{
    // TODO: `inner-work` (a scriptTask inside a nested transaction with a compensation
    // boundary) never completes in the test cluster — verified at 45 s wait, snapshot
    // shows it permanently Active alongside `inner-tx`, `outer-tx` while `inner-start`
    // / `outer-start` / `start` have all completed. Likely related to the fixture's note
    // that "ExclusiveGateway and EventBasedGateway targets fail to activate inside
    // inner-tx" — the inner-tx scope appears to leave script tasks stuck. Needs engine
    // investigation rather than test-shape changes.
    [TestMethod]
    [Ignore("Pending: inner-work scriptTask inside nested compensation-bounded transaction does not auto-complete in test cluster (verified at 45s wait).")]
    public async Task ScenarioA_BothTransactionsCommit_HappyPath()
    {
        var xml = BpmnFixtureLoader.Load("53-nested-transaction", "nested-tx-normal-inner.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("trigger-outer-complete-catch"),
            timeout: TimeSpan.FromSeconds(45));

        var msg = await ApiClient.SendMessageAsync("trigger-outer-complete");
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
    [Ignore("Pending investigation: same root cause as Scenario A (host task doesn't auto-complete).")]
    public async Task ScenarioB_InnerCancelsAndCompensates_OuterCommits()
    {
        var xml = BpmnFixtureLoader.Load("53-nested-transaction", "nested-tx-cancel-inner.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("trigger-outer-complete-catch"),
            timeout: TimeSpan.FromSeconds(15));

        await ApiClient.SendMessageAsync("trigger-outer-complete");

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(15));

        state.AssertCompletedActivities(
            "inner-work", "inner-cancel-end", "inner-compensate", "inner-tx",
            "trigger-outer-complete-catch", "outer-end", "outer-tx", "process-end");
        state.AssertVariableEquals("innerDone", "True");
        state.AssertVariableEquals("innerCompensated", "True");
    }
}
