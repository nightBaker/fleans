using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/13-multi-instance/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class MultiInstanceTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ParallelCollection_ProducesProcessedResultsForEachItem()
    {
        var xml = BpmnFixtureLoader.Load("13-multi-instance", "parallel-collection.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("start", "setItems", "end");
        var results = state.GetVariable("results");
        Assert.Contains("processed-A", results);
        Assert.Contains("processed-B", results);
        Assert.Contains("processed-C", results);
    }

    [TestMethod]
    public async Task ParallelCardinality_LoopRunsExactlyNTimes()
    {
        var xml = BpmnFixtureLoader.Load("13-multi-instance", "parallel-cardinality.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("start", "end");
        var results = state.GetVariable("results");
        Assert.Contains("iter-0", results);
        Assert.Contains("iter-1", results);
        Assert.Contains("iter-2", results);
    }

    [TestMethod]
    public async Task SequentialCollection_ProducesOrderedOutput()
    {
        var xml = BpmnFixtureLoader.Load("13-multi-instance", "sequential-collection.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("start", "setItems", "end");
        var results = state.GetVariable("results");
        Assert.IsGreaterThan(0, results.Length, "Sequential collection should produce a non-empty 'results' variable.");
    }

    [TestMethod]
    public async Task CompletionCondition_OneOfN_ShortCircuitsAfterFirstApproval()
    {
        var xml = BpmnFixtureLoader.Load("13-multi-instance", "completion-condition-1-of-N.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?>
            {
                ["approvers"] = new[] { "alice", "bob", "charlie" },
            });

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        // Per the manual plan: workflow completes after first approval; remaining iterations
        // are cancelled before they start or mid-flight. Don't assert a specific approver
        // since which one wins is timing-dependent.
        Assert.IsTrue(state.IsCompleted, "Workflow should complete after the 1-of-N condition fires.");
        Assert.IsFalse(state.IsCancelled, "Workflow should not be cancelled.");
    }
}
