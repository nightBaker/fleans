using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class UserTaskIntegrationTests : WorkflowTestBase
{
    private static IWorkflowDefinition CreateUserTaskWorkflow(
        string? assignee = null,
        List<string>? candidateGroups = null,
        List<string>? candidateUsers = null,
        List<string>? expectedOutputs = null)
    {
        var start = new StartEvent("start");
        var userTask = new UserTask("userTask1", assignee,
            candidateGroups ?? [], candidateUsers ?? [], expectedOutputs);
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "user-task-workflow",
            Activities = [start, userTask, end],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, userTask),
                new SequenceFlow("seq2", userTask, end)
            ]
        };
    }

    private async Task<IWorkflowInstanceGrain> StartUserTaskWorkflow(
        string? assignee = null,
        List<string>? candidateGroups = null,
        List<string>? candidateUsers = null,
        List<string>? expectedOutputs = null)
    {
        var workflow = CreateUserTaskWorkflow(assignee, candidateGroups, candidateUsers, expectedOutputs);
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        return grain;
    }

    // --- Workflow starts with user task active ---

    [TestMethod]
    public async Task StartWorkflow_ShouldLeaveUserTaskActive()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted);
        Assert.HasCount(1, snapshot.ActiveActivities);
        Assert.AreEqual("UserTask", snapshot.ActiveActivities[0].ActivityType);
    }

    // --- Claim lifecycle ---

    [TestMethod]
    public async Task ClaimUserTask_ShouldSucceed_WhenUserIsAssignee()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await grain.ClaimUserTask(instanceId, "john");

        // Verify task is still active (not completed yet)
        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted);
        Assert.HasCount(1, snapshot.ActiveActivities);
    }

    [TestMethod]
    public async Task ClaimUserTask_ShouldFail_WhenWrongAssignee()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await grain.ClaimUserTask(instanceId, "alice");
        });
    }

    [TestMethod]
    public async Task ClaimUserTask_ShouldFail_WhenNotInCandidateUsers()
    {
        var grain = await StartUserTaskWorkflow(candidateUsers: ["alice", "bob"]);

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await grain.ClaimUserTask(instanceId, "charlie");
        });
    }

    // --- Unclaim ---

    [TestMethod]
    public async Task UnclaimUserTask_ShouldSucceed_AfterClaim()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await grain.ClaimUserTask(instanceId, "john");
        await grain.UnclaimUserTask(instanceId);

        // Task should still be active
        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted);
    }

    // --- Complete user task ---

    [TestMethod]
    public async Task CompleteUserTask_ShouldCompleteWorkflow()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await grain.ClaimUserTask(instanceId, "john");
        await grain.CompleteUserTask(instanceId, "john", new ExpandoObject());

        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
    }

    [TestMethod]
    public async Task CompleteUserTask_ShouldMergeVariables()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await grain.ClaimUserTask(instanceId, "john");

        dynamic vars = new ExpandoObject();
        vars.approval = "approved";
        vars.comment = "Looks good";

        await grain.CompleteUserTask(instanceId, "john", vars);

        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        // Verify variables were merged
        Assert.IsTrue(snapshot.VariableStates.Count > 0);
    }

    [TestMethod]
    public async Task CompleteUserTask_ShouldFail_WhenNotClaimed()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await grain.CompleteUserTask(instanceId, "john", new ExpandoObject());
        });
    }

    [TestMethod]
    public async Task CompleteUserTask_ShouldFail_WhenWrongUser()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await grain.ClaimUserTask(instanceId, "john");

        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await grain.CompleteUserTask(instanceId, "alice", new ExpandoObject());
        });
    }

    [TestMethod]
    public async Task CompleteUserTask_ShouldValidateExpectedOutputVariables()
    {
        var grain = await StartUserTaskWorkflow(
            assignee: "john",
            expectedOutputs: ["approval", "comment"]);

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await grain.ClaimUserTask(instanceId, "john");

        // Complete with missing variables should fail
        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await grain.CompleteUserTask(instanceId, "john", new ExpandoObject());
        });
    }

    [TestMethod]
    public async Task CompleteUserTask_ShouldSucceed_WithRequiredOutputVariables()
    {
        var grain = await StartUserTaskWorkflow(
            assignee: "john",
            expectedOutputs: ["approval"]);

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await grain.ClaimUserTask(instanceId, "john");

        dynamic vars = new ExpandoObject();
        vars.approval = "approved";

        await grain.CompleteUserTask(instanceId, "john", vars);

        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
    }

    // --- CompleteActivity guard ---

    [TestMethod]
    public async Task CompleteActivity_ShouldThrow_ForUserTask()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await grain.CompleteActivity("userTask1", new ExpandoObject());
        });
    }

    // --- Registry queries ---

    [TestMethod]
    public async Task UserTaskRegistry_ShouldTrackPendingTasks()
    {
        var grain = await StartUserTaskWorkflow(
            assignee: "john", candidateGroups: ["managers"]);

        var registry = Cluster.GrainFactory.GetGrain<IUserTaskRegistryGrain>(0);
        var tasks = await registry.GetPendingTasks();

        Assert.IsTrue(tasks.Count >= 1);
        var task = tasks.First(t => t.ActivityId == "userTask1");
        Assert.AreEqual("john", task.Assignee);
        Assert.IsTrue(task.CandidateGroups.Contains("managers"));
    }

    [TestMethod]
    public async Task UserTaskRegistry_ShouldUnregisterAfterCompletion()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        await grain.ClaimUserTask(instanceId, "john");
        await grain.CompleteUserTask(instanceId, "john", new ExpandoObject());

        var registry = Cluster.GrainFactory.GetGrain<IUserTaskRegistryGrain>(0);
        var task = await registry.GetTask(instanceId);

        Assert.IsNull(task);
    }

    [TestMethod]
    public async Task UserTaskRegistry_ShouldFilterByAssignee()
    {
        await StartUserTaskWorkflow(assignee: "john");
        await StartUserTaskWorkflow(assignee: "alice");

        var registry = Cluster.GrainFactory.GetGrain<IUserTaskRegistryGrain>(0);

        var johnTasks = await registry.GetPendingTasks(assignee: "john");
        Assert.IsTrue(johnTasks.All(t => t.Assignee == "john" || t.CandidateUsers.Contains("john")));

        var aliceTasks = await registry.GetPendingTasks(assignee: "alice");
        Assert.IsTrue(aliceTasks.All(t => t.Assignee == "alice" || t.CandidateUsers.Contains("alice")));
    }

    // --- FailActivity tests ---

    [TestMethod]
    public async Task FailActivity_GenericException_ShouldSetErrorCode500()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var exception = new Exception("Something went wrong");
        await grain.FailActivity("userTask1", exception);

        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(snapshot);

        var failedActivity = snapshot.CompletedActivities
            .FirstOrDefault(a => a.ActivityId == "userTask1");
        Assert.IsNotNull(failedActivity);
        Assert.IsNotNull(failedActivity.ErrorState);
        Assert.AreEqual(500, failedActivity.ErrorState.Code);
        Assert.AreEqual("Something went wrong", failedActivity.ErrorState.Message);
    }

    [TestMethod]
    public async Task FailActivity_BadRequestException_ShouldSetErrorCode400()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var exception = new BadRequestActivityException("Invalid input");
        await grain.FailActivity("userTask1", exception);

        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(snapshot);

        var failedActivity = snapshot.CompletedActivities
            .FirstOrDefault(a => a.ActivityId == "userTask1");
        Assert.IsNotNull(failedActivity);
        Assert.IsNotNull(failedActivity.ErrorState);
        Assert.AreEqual(400, failedActivity.ErrorState.Code);
        Assert.AreEqual("Invalid input", failedActivity.ErrorState.Message);
    }

    [TestMethod]
    public async Task FailActivity_ShouldTransitionToNextActivity()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var exception = new Exception("Failure");
        await grain.FailActivity("userTask1", exception);

        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(snapshot);

        // Failed activity should be in completed list and workflow should progress
        var failedEntry = snapshot.CompletedActivities
            .FirstOrDefault(a => a.ActivityId == "userTask1");
        Assert.IsNotNull(failedEntry);

        // User task should no longer be active
        Assert.IsFalse(snapshot.ActiveActivities.Any(a => a.ActivityId == "userTask1"));
    }

    [TestMethod]
    public async Task FailActivity_ShouldNotMergeVariables()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        // Get initial variable state count
        var beforeSnapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(beforeSnapshot);
        var initialVarCount = beforeSnapshot.VariableStates.Count;

        var exception = new Exception("Failure");
        await grain.FailActivity("userTask1", exception);

        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.IsNotNull(snapshot);

        // No new variable states should be added on failure
        Assert.AreEqual(initialVarCount, snapshot.VariableStates.Count);
    }

    [TestMethod]
    public async Task FailActivity_ShouldUnregisterFromRegistry()
    {
        var grain = await StartUserTaskWorkflow(assignee: "john");

        var activeActivities = await grain.GetActiveActivities();
        Guid instanceId = default;
        foreach (var a in activeActivities)
        {
            if (await a.GetActivityId() == "userTask1")
            {
                instanceId = await a.GetActivityInstanceId();
                break;
            }
        }

        var exception = new Exception("Failure");
        await grain.FailActivity("userTask1", exception);

        var registry = Cluster.GrainFactory.GetGrain<IUserTaskRegistryGrain>(0);
        var task = await registry.GetTask(instanceId);

        Assert.IsNull(task);
    }
}
