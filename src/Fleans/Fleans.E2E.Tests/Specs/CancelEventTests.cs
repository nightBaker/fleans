using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/30-cancel-event/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class CancelEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task CancelEndInTransaction_FiresCancelBoundary_RecoveryRuns()
    {
        var xml = BpmnFixtureLoader.Load("30-cancel-event", "cancel-transaction.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        // `review_task` is a <userTask> inside the transaction. Wait for it to surface,
        // then complete it via /UserTasks/{id}/complete (not /Execution/complete-activity —
        // the latter returns 409 Conflict on user-task instances).
        var withTask = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("review_task"));

        var reviewActivity = withTask.ActiveActivities
            .First(a => a.ActivityId == "review_task");

        // The fixture's userTask declares no assignee / candidateUsers / candidateGroups,
        // so any caller may claim it.
        using (var claim = await ApiClient.ClaimUserTaskAsync(reviewActivity.ActivityInstanceId, "tester"))
        {
            Assert.IsTrue(claim.IsSuccessStatusCode,
                $"Claim should succeed for an unconstrained user task; got {claim.StatusCode}.");
        }

        using (var complete = await ApiClient.CompleteUserTaskAsync(
            reviewActivity.ActivityInstanceId, "tester"))
        {
            Assert.IsTrue(complete.IsSuccessStatusCode,
                $"Complete should succeed; got {complete.StatusCode}.");
        }

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId,
            timeout: TimeSpan.FromSeconds(15));

        state.AssertCompletedActivities(
            "review_task", "cancel_end", "cancel_boundary", "recovery_task", "end");
        Assert.IsEmpty(state.ActiveActivityIds);
    }
}
