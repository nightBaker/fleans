using System.Net;
using Fleans.Application.DTOs;
using Fleans.Application.QueryModels;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/45-user-task-fail-cancel/test-plan.md
// Ports tests/manual/61-usertask-group-claim/test-plan.md
// Covers the UserTasksController endpoints that UserTaskTests (plan 18) doesn't reach:
// fail, cancel, unclaim + re-claim, the pending-list filters, candidateGroups claims
// (rule (c) of the three-rule OR — the test cluster runs without auth, so
// BodyUserGroupResolver reads `userGroups` from the request body), and fail/cancel
// idempotency on an already-terminal task (#536).
[TestClass]
[TestCategory("E2E")]
public class UserTaskLifecycleTests : WorkflowE2ETestBase
{
    private const string FailCancelPlan = "45-user-task-fail-cancel";
    private const string FailCancelFixture = "user-task-simple.bpmn";
    private const string FailCancelTask = "review-task";
    private const string FailCancelAssignee = "test-user";

    private const string GroupClaimPlan = "61-usertask-group-claim";
    private const string GroupClaimFixture = "group-claim.bpmn";
    private const string GroupClaimTask = "ApproveTask";

    // ---------------------------------------------------------------- fail

    [TestMethod]
    public async Task Fail_UnmatchedErrorCode_ActivityFailsWithoutBoundary_AndRepeatIsIdempotent()
    {
        var (instanceId, taskId) = await StartAndGetTaskAsync(FailCancelPlan, FailCancelFixture, FailCancelTask);
        await ClaimAndWaitAsync(taskId, FailCancelAssignee);

        // "500" does not match the boundary's errorCode "400" → no boundary path.
        using (var fail = await ApiClient.FailUserTaskAsync(
            taskId, new FailTaskRequest("User rejected the task", "500")))
        {
            Assert.AreEqual(HttpStatusCode.OK, fail.StatusCode);
        }

        var state = await ApiClient.WaitForStateAsync(
            instanceId,
            s => s.CompletedActivities.Any(a => a.ActivityId == FailCancelTask));
        var failed = state.CompletedActivities.Single(a => a.ActivityId == FailCancelTask);
        Assert.IsNotNull(failed.ErrorState, "Failed user task must carry an ErrorState.");
        Assert.AreEqual("500", failed.ErrorState.Code);
        Assert.IsFalse(failed.IsCancelled, "A failed task is not a cancelled task.");
        Assert.DoesNotContain(FailCancelTask, state.ActiveActivityIds);
        state.AssertNotCompleted("error-boundary", "error-end", "end");

        // Task drops out of the pending surface.
        Assert.IsNull(await ApiClient.WaitForUserTaskAsync(taskId, t => t is null));

        // Idempotency (#536): fail and cancel on an already-terminal task → 200, no new events.
        using (var again = await ApiClient.FailUserTaskAsync(taskId, new FailTaskRequest("again", "500")))
        {
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
        }
        using (var cancelAfterFail = await ApiClient.CancelUserTaskAsync(taskId, new CancelTaskRequest("late")))
        {
            Assert.AreEqual(HttpStatusCode.OK, cancelAfterFail.StatusCode);
        }

        var after = await ApiClient.GetStateAsync(instanceId);
        Assert.IsNotNull(after);
        var stillFailed = after.CompletedActivities.Single(a => a.ActivityId == FailCancelTask);
        Assert.AreEqual("500", stillFailed.ErrorState?.Code, "Repeat calls must not rewrite the error state.");
        Assert.IsFalse(stillFailed.IsCancelled, "Cancel after fail must be a no-op.");
        after.AssertNotCompleted("error-boundary", "end");
    }

    [TestMethod]
    public async Task Fail_ErrorCodeMatchesBoundary_RoutesViaErrorBoundary()
    {
        var (instanceId, taskId) = await StartAndGetTaskAsync(FailCancelPlan, FailCancelFixture, FailCancelTask);
        await ClaimAndWaitAsync(taskId, FailCancelAssignee);

        using (var fail = await ApiClient.FailUserTaskAsync(
            taskId, new FailTaskRequest("Validation failed", "400")))
        {
            Assert.AreEqual(HttpStatusCode.OK, fail.StatusCode);
        }

        var state = await ApiClient.WaitForStateAsync(
            instanceId,
            s => s.CompletedActivityIds.Contains("error-boundary"));
        state.AssertCompletedActivities(FailCancelTask, "error-boundary");
        state.AssertNotCompleted("end");
        Assert.DoesNotContain(FailCancelTask, state.ActiveActivityIds);

        Assert.IsNull(await ApiClient.WaitForUserTaskAsync(taskId, t => t is null));
    }

    [TestMethod]
    public async Task Fail_ValidationAndUnknownTask_Return400And404()
    {
        // Unknown task id → 404.
        using (var unknown = await ApiClient.FailUserTaskAsync(Guid.Empty, new FailTaskRequest("test")))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        using (var unknownCancel = await ApiClient.CancelUserTaskAsync(Guid.NewGuid()))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, unknownCancel.StatusCode);
        }
        using (var unknownUnclaim = await ApiClient.UnclaimUserTaskAsync(Guid.NewGuid()))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, unknownUnclaim.StatusCode);
        }

        // Missing ErrorMessage on a live task → 400, and the task stays pending.
        var (_, taskId) = await StartAndGetTaskAsync(FailCancelPlan, FailCancelFixture, FailCancelTask);
        using (var missing = await ApiClient.FailUserTaskRawAsync(taskId, """{ "errorCode": "500" }"""))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        }
        var task = await ApiClient.GetUserTaskAsync(taskId);
        Assert.IsNotNull(task, "A rejected fail request must not touch the task.");
        Assert.AreEqual("Created", task.TaskState);
    }

    // -------------------------------------------------------------- cancel

    [TestMethod]
    public async Task Cancel_WithReason_TerminatesBranch_AndRepeatIsIdempotent()
    {
        var (instanceId, taskId) = await StartAndGetTaskAsync(FailCancelPlan, FailCancelFixture, FailCancelTask);
        await ClaimAndWaitAsync(taskId, FailCancelAssignee);

        using (var cancel = await ApiClient.CancelUserTaskAsync(taskId, new CancelTaskRequest("Operator cancelled")))
        {
            Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        }

        var state = await ApiClient.WaitForStateAsync(
            instanceId,
            s => s.CompletedActivities.Any(a => a.ActivityId == FailCancelTask && a.IsCancelled));
        var cancelled = state.CompletedActivities.Single(a => a.ActivityId == FailCancelTask);
        Assert.AreEqual("Operator cancelled", cancelled.CancellationReason);
        Assert.IsNull(cancelled.ErrorState, "A cancelled task is not a failed task.");
        Assert.DoesNotContain(FailCancelTask, state.ActiveActivityIds);
        state.AssertNotCompleted("end", "error-boundary");

        Assert.IsNull(await ApiClient.WaitForUserTaskAsync(taskId, t => t is null));

        // Idempotency (#536): cancel and fail on an already-cancelled task → 200, no-op.
        using (var again = await ApiClient.CancelUserTaskAsync(taskId, new CancelTaskRequest("again")))
        {
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
        }
        using (var failAfterCancel = await ApiClient.FailUserTaskAsync(taskId, new FailTaskRequest("late", "400")))
        {
            Assert.AreEqual(HttpStatusCode.OK, failAfterCancel.StatusCode);
        }

        var after = await ApiClient.GetStateAsync(instanceId);
        Assert.IsNotNull(after);
        var stillCancelled = after.CompletedActivities.Single(a => a.ActivityId == FailCancelTask);
        Assert.AreEqual("Operator cancelled", stillCancelled.CancellationReason,
            "Repeat cancel must not rewrite the cancellation reason.");
        Assert.IsNull(stillCancelled.ErrorState, "Fail after cancel must be a no-op.");
        after.AssertNotCompleted("error-boundary", "end");
    }

    [TestMethod]
    public async Task Cancel_WithoutBody_ReasonIsOptional()
    {
        var (instanceId, taskId) = await StartAndGetTaskAsync(FailCancelPlan, FailCancelFixture, FailCancelTask);

        // Unclaimed task, empty body.
        using (var cancel = await ApiClient.CancelUserTaskAsync(taskId))
        {
            Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        }

        var state = await ApiClient.WaitForStateAsync(
            instanceId,
            s => s.CompletedActivities.Any(a => a.ActivityId == FailCancelTask && a.IsCancelled));
        Assert.DoesNotContain(FailCancelTask, state.ActiveActivityIds);
        state.AssertNotCompleted("end");

        Assert.IsNull(await ApiClient.WaitForUserTaskAsync(taskId, t => t is null));
    }

    // ------------------------------------------------- unclaim + re-claim

    [TestMethod]
    public async Task Unclaim_ThenReclaimByAnotherUser_NewClaimantCompletes()
    {
        var (instanceId, taskId) = await StartAndGetTaskAsync(GroupClaimPlan, GroupClaimFixture, GroupClaimTask);

        using (var claim = await ApiClient.ClaimUserTaskAsync(taskId, "alice", ["managers"]))
        {
            Assert.AreEqual(HttpStatusCode.OK, claim.StatusCode);
        }
        await ApiClient.WaitForUserTaskAsync(taskId, t => t is { TaskState: "Claimed", ClaimedBy: "alice" });

        using (var unclaim = await ApiClient.UnclaimUserTaskAsync(taskId))
        {
            Assert.AreEqual(HttpStatusCode.OK, unclaim.StatusCode);
        }
        var released = await ApiClient.WaitForUserTaskAsync(
            taskId, t => t is { TaskState: "Created", ClaimedBy: null });
        Assert.IsNotNull(released);

        // alice no longer holds the claim — she cannot complete it.
        using (var aliceComplete = await ApiClient.CompleteUserTaskAsync(taskId, "alice"))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, aliceComplete.StatusCode);
        }

        // Another user (via a different candidate group) claims it.
        using (var reclaim = await ApiClient.ClaimUserTaskAsync(taskId, "bob", ["supervisors"]))
        {
            Assert.AreEqual(HttpStatusCode.OK, reclaim.StatusCode);
        }
        await ApiClient.WaitForUserTaskAsync(taskId, t => t is { TaskState: "Claimed", ClaimedBy: "bob" });

        using (var complete = await ApiClient.CompleteUserTaskAsync(taskId, "bob"))
        {
            Assert.AreEqual(HttpStatusCode.OK, complete.StatusCode);
        }

        var final = await ApiClient.WaitForCompletionAsync(instanceId);
        final.AssertCompletedActivities("start", GroupClaimTask, "end");
        Assert.IsEmpty(final.ActiveActivityIds);
        Assert.IsNull(await ApiClient.WaitForUserTaskAsync(taskId, t => t is null));
    }

    // ------------------------------------------ candidateGroups claim (61)

    [TestMethod]
    public async Task GroupClaim_OnlyIntersectingGroupMayClaim()
    {
        var (instanceId, taskId) = await StartAndGetTaskAsync(GroupClaimPlan, GroupClaimFixture, GroupClaimTask);

        var task = await ApiClient.GetUserTaskAsync(taskId);
        Assert.IsNotNull(task);
        Assert.IsNull(task.Assignee);
        Assert.IsEmpty(task.CandidateUsers);
        CollectionAssert.AreEquivalent(new[] { "managers", "supervisors" }, task.CandidateGroups.ToList());

        // Non-intersecting group → 409 with an identifier-free message.
        using (var rejected = await ApiClient.ClaimUserTaskAsync(taskId, "bob", ["unrelated"]))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode);
            var body = await rejected.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "User bob is not authorized to claim this task");
            Assert.DoesNotContain("managers", body, "Rejection must not leak candidate-group names.");
            Assert.DoesNotContain("supervisors", body, "Rejection must not leak candidate-group names.");
        }

        // Legacy wire shape (no userGroups) on a group-gated task → 409 (pre-#588 bypass).
        using (var noGroups = await ApiClient.ClaimUserTaskAsync(taskId, "bob"))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, noGroups.StatusCode);
        }

        // Group matching is ordinal case-sensitive.
        using (var wrongCase = await ApiClient.ClaimUserTaskAsync(taskId, "bob", ["Managers"]))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, wrongCase.StatusCode);
        }

        var stillOpen = await ApiClient.GetUserTaskAsync(taskId);
        Assert.IsNotNull(stillOpen);
        Assert.AreEqual("Created", stillOpen.TaskState, "Rejected claims must not change task state.");
        Assert.IsNull(stillOpen.ClaimedBy);

        // Intersecting group (one of several supplied) → 200.
        using (var claim = await ApiClient.ClaimUserTaskAsync(taskId, "alice", ["unrelated", "managers"]))
        {
            Assert.AreEqual(HttpStatusCode.OK, claim.StatusCode);
        }
        var claimed = await ApiClient.WaitForUserTaskAsync(
            taskId, t => t is { TaskState: "Claimed", ClaimedBy: "alice" });
        Assert.IsNotNull(claimed);

        using (var complete = await ApiClient.CompleteUserTaskAsync(taskId, "alice"))
        {
            Assert.AreEqual(HttpStatusCode.OK, complete.StatusCode);
        }
        var final = await ApiClient.WaitForCompletionAsync(instanceId);
        final.AssertCompletedActivities(GroupClaimTask, "end");
    }

    [TestMethod]
    public async Task GroupClaim_UnrestrictedTask_LegacyBodyWithoutGroupsStillClaims()
    {
        // Plan 61 step 5 — backward compat. The plan-45 fixture constrains only by assignee,
        // so the assignee claims with the legacy DTO shape (no userGroups field).
        var (_, taskId) = await StartAndGetTaskAsync(FailCancelPlan, FailCancelFixture, FailCancelTask);
        using (var claim = await ApiClient.ClaimUserTaskAsync(taskId, FailCancelAssignee))
        {
            Assert.AreEqual(HttpStatusCode.OK, claim.StatusCode);
        }
        await ApiClient.WaitForUserTaskAsync(
            taskId, t => t is { TaskState: "Claimed" } && t.ClaimedBy == FailCancelAssignee);

        // Clean up so the instance doesn't linger as an open task.
        using var cancel = await ApiClient.CancelUserTaskAsync(taskId);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
    }

    // ------------------------------------------------- pending list filters

    [TestMethod]
    public async Task PendingList_FiltersByAssigneeAndCandidateGroup()
    {
        // Plan-18 fixture: assignee=john, candidateUsers=john,bob, candidateGroups=managers,leads.
        var (approvalInstance, approvalTask) = await StartAndGetTaskAsync("18-user-task", "user-task-approval.bpmn", "review");
        // Plan-61 fixture: candidateGroups=managers,supervisors only.
        var (_, groupTask) = await StartAndGetTaskAsync(GroupClaimPlan, GroupClaimFixture, GroupClaimTask);

        // assignee filter matches Assignee OR CandidateUsers.
        var john = await PendingIdsAsync(assignee: "john");
        Assert.Contains(approvalTask, john);
        Assert.DoesNotContain(groupTask, john);

        var bob = await PendingIdsAsync(assignee: "bob");
        Assert.Contains(approvalTask, bob, "candidateUsers member must match the assignee filter.");

        var charlie = await PendingIdsAsync(assignee: "charlie");
        Assert.DoesNotContain(approvalTask, charlie);
        Assert.DoesNotContain(groupTask, charlie);

        // candidateGroup filter.
        var managers = await PendingIdsAsync(candidateGroup: "managers");
        Assert.Contains(approvalTask, managers);
        Assert.Contains(groupTask, managers);

        var supervisors = await PendingIdsAsync(candidateGroup: "supervisors");
        Assert.Contains(groupTask, supervisors);
        Assert.DoesNotContain(approvalTask, supervisors);

        var leads = await PendingIdsAsync(candidateGroup: "leads");
        Assert.Contains(approvalTask, leads);
        Assert.DoesNotContain(groupTask, leads);

        // Combined filters AND together.
        var johnLeads = await PendingIdsAsync(assignee: "john", candidateGroup: "leads");
        Assert.Contains(approvalTask, johnLeads);
        var johnSupervisors = await PendingIdsAsync(assignee: "john", candidateGroup: "supervisors");
        Assert.DoesNotContain(approvalTask, johnSupervisors);
        Assert.DoesNotContain(groupTask, johnSupervisors);

        // Unfiltered list has both, with the response shape populated.
        var all = await ApiClient.GetPendingUserTasksAsync();
        var approvalRow = all.Items.SingleOrDefault(t => t.ActivityInstanceId == approvalTask);
        Assert.IsNotNull(approvalRow);
        Assert.AreEqual(approvalInstance, approvalRow.WorkflowInstanceId);
        Assert.AreEqual("review", approvalRow.ActivityId);
        Assert.IsTrue(all.Items.Any(t => t.ActivityInstanceId == groupTask));
        Assert.IsGreaterThanOrEqualTo(2, all.TotalCount);

        // Once the task leaves the pending state it drops out of the list.
        using (var cancel = await ApiClient.CancelUserTaskAsync(groupTask))
        {
            Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        }
        await ApiClient.WaitForUserTaskAsync(groupTask, t => t is null);
        Assert.DoesNotContain(groupTask, await PendingIdsAsync(candidateGroup: "managers"));

        using (var cancelApproval = await ApiClient.CancelUserTaskAsync(approvalTask))
        {
            Assert.AreEqual(HttpStatusCode.OK, cancelApproval.StatusCode);
        }
    }

    // ---------------------------------------------------------- helpers

    private async Task<(Guid InstanceId, Guid TaskId)> StartAndGetTaskAsync(
        string plan, string fixture, string userTaskActivityId)
    {
        var deployed = await ApiClient.DeployAsync(BpmnFixtureLoader.Load(plan, fixture));
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForStateAsync(
            started.WorkflowInstanceId,
            s => s.ActiveActivityIds.Contains(userTaskActivityId));
        var taskId = state.ActiveActivities.Single(a => a.ActivityId == userTaskActivityId).ActivityInstanceId;

        // The user-task projection is written asynchronously; wait until it's queryable.
        await ApiClient.WaitForUserTaskAsync(taskId, t => t is not null);
        return (started.WorkflowInstanceId, taskId);
    }

    private async Task ClaimAndWaitAsync(Guid taskId, string userId)
    {
        using (var claim = await ApiClient.ClaimUserTaskAsync(taskId, userId))
        {
            Assert.AreEqual(HttpStatusCode.OK, claim.StatusCode, $"Claim by {userId} should succeed.");
        }
        await ApiClient.WaitForUserTaskAsync(taskId, t => t is { TaskState: "Claimed" } && t.ClaimedBy == userId);
    }

    private async Task<List<Guid>> PendingIdsAsync(string? assignee = null, string? candidateGroup = null)
    {
        PagedResult<UserTaskResponse> page = await ApiClient.GetPendingUserTasksAsync(assignee, candidateGroup);
        return page.Items.Select(t => t.ActivityInstanceId).ToList();
    }
}
