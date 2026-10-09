using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/24-conditional-event/test-plan.md (Tests C + D — Conditional Boundary on a waiting task)
[TestClass]
[TestCategory("E2E")]
public class ConditionalBoundaryEventTests : WorkflowE2ETestBase
{
    private const string PlanFolder = "24-conditional-event";

    [TestMethod]
    [Ignore("Blocked by #784: non-interrupting boundary deliveries merge into a cloned scope the conditional watcher never reads.")]
    public async Task InterruptingConditionalBoundary_ConditionBecomesTrue_CancelsHostAndTakesBoundaryPath()
    {
        var orderId = $"cond-{Guid.NewGuid():N}";
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(PlanFolder, "conditional-boundary-on-task.bpmn"));
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            new Dictionary<string, object?> { ["orderId"] = orderId });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("hostTask"));

        // The non-interrupting message boundary merges `decision` into the host's scope;
        // the execution loop then re-evaluates the conditional watcher.
        var delivery = await ApiClient.SendMessageAsync(
            "conditionalBoundaryUpdate",
            correlationKey: orderId,
            variables: new Dictionary<string, object?> { ["decision"] = "escalate" });
        Assert.IsTrue(delivery.Delivered, "Update message should be delivered to the host's message boundary.");

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("condBoundary", "escalated", "endBoundary");
        state.AssertNotCompleted("endNormal");
        state.AssertVariableEquals("handled", "boundary-fired");
        var host = state.CompletedActivities.Single(a => a.ActivityId == "hostTask");
        Assert.IsTrue(host.IsCancelled, "Interrupting conditional boundary must cancel the host task.");
    }

    [TestMethod]
    public async Task InterruptingConditionalBoundary_ConditionStaysFalse_HostCompletesNormally()
    {
        var orderId = $"cond-{Guid.NewGuid():N}";
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(PlanFolder, "conditional-boundary-on-task.bpmn"));
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            new Dictionary<string, object?> { ["orderId"] = orderId });

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("hostTask"));

        var delivery = await ApiClient.SendMessageAsync(
            "conditionalBoundaryUpdate",
            correlationKey: orderId,
            variables: new Dictionary<string, object?> { ["decision"] = "approve" });
        Assert.IsTrue(delivery.Delivered);

        // Condition is false → boundary must not fire; host stays active until completed.
        var mid = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.CompletedActivityIds.Contains("endUpdate"));
        CollectionAssert.Contains(mid.ActiveActivityIds, "hostTask");
        mid.AssertNotCompleted("escalated");

        using (var resp = await ApiClient.CompleteActivityAsync(started.WorkflowInstanceId, "hostTask"))
        {
            Assert.IsTrue(resp.IsSuccessStatusCode,
                $"complete-activity on hostTask should succeed; got {(int)resp.StatusCode}.");
        }

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        state.AssertCompletedActivities("hostTask", "endNormal");
        state.AssertNotCompleted("condBoundary", "escalated", "endBoundary");
        Assert.IsFalse(
            state.CompletedActivities.Single(a => a.ActivityId == "hostTask").IsCancelled,
            "Host task should complete normally, not be cancelled.");
    }

    [TestMethod]
    [Ignore("Blocked by #784: conditional watcher only sees the host's own scope; parallel branches write to a cloned scope.")]
    public async Task InterruptingConditionalBoundary_VariableChangedInParallelBranch_Fires()
    {
        var deployed = await ApiClient.DeployAsync(
            BpmnFixtureLoader.Load(PlanFolder, "conditional-boundary-parallel-branch.bpmn"));
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("hostTask") && s.ActiveActivityIds.Contains("triggerTask"));

        using (var resp = await ApiClient.CompleteActivityAsync(
            started.WorkflowInstanceId,
            "triggerTask",
            new Dictionary<string, object> { ["decision"] = "escalate" }))
        {
            Assert.IsTrue(resp.IsSuccessStatusCode,
                $"complete-activity on triggerTask should succeed; got {(int)resp.StatusCode}.");
        }

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("triggerTask", "endTrigger", "escalated", "endBoundary");
        state.AssertNotCompleted("endNormal");
        Assert.IsTrue(state.CompletedActivities.Single(a => a.ActivityId == "hostTask").IsCancelled);
    }
}
