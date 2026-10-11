using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/13-multi-instance/test-plan.md (Scenarios 13g–13i — MI sub-process, MI call activity, one iteration fails)
[TestClass]
[TestCategory("E2E")]
public class MultiInstanceScopeTests : WorkflowE2ETestBase
{
    private const string PlanFolder = "13-multi-instance";

    [TestMethod]
    public async Task ParallelMultiInstanceSubProcess_RunsBodyOncePerItem_AndCollectsOutput()
    {
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(PlanFolder, "parallel-subprocess.bpmn"));
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("setItems", "perItem", "end");
        Assert.AreEqual(3, state.CompletedActivities.Count(a => a.ActivityId == "processItem"),
            "The sub-process body must run once per collection item.");
        var results = state.GetVariable("results");
        Assert.Contains("sub-A", results);
        Assert.Contains("sub-B", results);
        Assert.Contains("sub-C", results);
    }

    [TestMethod]
    [Ignore("Blocked by #782: child completion is routed by ActivityId, which MI iterations share, so the MI call activity never completes.")]
    public async Task ParallelMultiInstanceCallActivity_SpawnsOneChildPerItem_AndCollectsOutput()
    {
        await ApiClient.DeployAsync(BpmnFixtureLoader.Load(PlanFolder, "mi-child.bpmn"));
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(PlanFolder, "parallel-call-activity.bpmn"));
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("setItems", "callChild", "end");
        var childIds = state.CompletedActivities
            .Where(a => a.ActivityId == "callChild" && a.ChildWorkflowInstanceId is not null)
            .Select(a => a.ChildWorkflowInstanceId!.Value)
            .Distinct()
            .ToList();
        Assert.HasCount(3, childIds, "One child workflow instance per collection item.");
        foreach (var childId in childIds)
        {
            var child = await ApiClient.GetStateAsync(childId);
            Assert.IsNotNull(child);
            Assert.IsTrue(child.IsCompleted, $"Child {childId} should be completed.");
            child.AssertCompletedActivities("childScript", "childEnd");
        }

        var results = state.GetVariable("results");
        Assert.Contains("child-A", results);
        Assert.Contains("child-B", results);
        Assert.Contains("child-C", results);
    }

    [TestMethod]
    public async Task ParallelMultiInstanceSubProcess_OneIterationFails_HostFailsAndErrorBoundaryCatches()
    {
        var deployed = await ApiClient.DeployAsync(
            BpmnFixtureLoader.Load(PlanFolder, "parallel-subprocess-one-fails.bpmn"));
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        Assert.IsFalse(state.IsCancelled, "The instance should complete via the error boundary, not be cancelled.");
        state.AssertCompletedActivities("miError", "errorHandler", "errorEnd");
        state.AssertNotCompleted("end");
        state.AssertVariableEquals("errorHandled", "True");
        Assert.IsEmpty(state.ActiveActivityIds, "No iteration may be left running after the host fails.");

        var failedBodies = state.CompletedActivities
            .Where(a => a.ActivityId == "processItem" && a.ErrorState is not null)
            .ToList();
        Assert.HasCount(1, failedBodies, "Exactly one iteration (item B) should fail.");
        Assert.AreEqual("500", failedBodies[0].ErrorState!.Code);
    }
}
