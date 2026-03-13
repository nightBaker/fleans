using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
        var userTaskEntry = activeActivities.First(a => a.GetActivityId().Result == "userTask1");
        var instanceId = await userTaskEntry.GetActivityInstanceId();

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
}
