using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/18-user-task/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class UserTaskTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task UserTaskLifecycle_ListClaimCompleteAndWorkflowContinues()
    {
        var xml = BpmnFixtureLoader.Load("18-user-task", "user-task-approval.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        // Wait for the user task to be active.
        var stateWithTask = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains("review"));

        var reviewActivity = stateWithTask.ActiveActivities.FirstOrDefault(a => a.ActivityId == "review");
        Assert.IsNotNull(reviewActivity, "review user task should be active.");
        var taskId = reviewActivity.ActivityInstanceId;

        // GET single task — assignee + groups + users + state.
        var task = await ApiClient.GetUserTaskAsync(taskId);
        Assert.IsNotNull(task);
        Assert.AreEqual("review", task.ActivityId);
        Assert.AreEqual("john", task.Assignee);
        Assert.Contains("managers", task.CandidateGroups);
        Assert.Contains("john", task.CandidateUsers);

        // Unauthorized user cannot claim — expect 409 Conflict.
        using (var rejected = await ApiClient.ClaimUserTaskAsync(taskId, "charlie"))
        {
            Assert.AreEqual(System.Net.HttpStatusCode.Conflict, rejected.StatusCode);
        }

        // Authorized claim succeeds.
        using (var claim = await ApiClient.ClaimUserTaskAsync(taskId, "john"))
        {
            Assert.IsTrue(claim.IsSuccessStatusCode, $"Claim by john should succeed; got {claim.StatusCode}.");
        }

        // Complete without required outputs — expect 409 Conflict.
        using (var partial = await ApiClient.CompleteUserTaskAsync(taskId, "john", new Dictionary<string, object?>()))
        {
            Assert.AreEqual(System.Net.HttpStatusCode.Conflict, partial.StatusCode);
        }

        // Complete with required outputs.
        using (var complete = await ApiClient.CompleteUserTaskAsync(
            taskId, "john",
            new Dictionary<string, object?>
            {
                ["approved"] = true,
                ["reviewComment"] = "Looks good",
            }))
        {
            Assert.IsTrue(complete.IsSuccessStatusCode, $"Complete should succeed; got {complete.StatusCode}.");
        }

        var final = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        final.AssertCompletedActivities("start", "prepare", "review", "postReview", "end");
        Assert.IsEmpty(final.ActiveActivityIds);
        final.AssertVariableEquals("approved", "True");

        // Task should no longer be registered.
        Assert.IsNull(await ApiClient.GetUserTaskAsync(taskId),
            "Task should be unregistered after completion.");
    }
}
