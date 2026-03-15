using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class UserTaskLifecycleTests
{
    // --- Helpers ---

    private static (WorkflowExecution execution, WorkflowInstanceState state, ActivityInstanceEntry userTaskEntry)
        CreateWithExecutingUserTask(
            string? assignee = null,
            List<string>? candidateGroups = null,
            List<string>? candidateUsers = null,
            List<string>? expectedOutputs = null)
    {
        var start = new StartEvent("start1");
        var userTask = new UserTask("userTask1", assignee, candidateGroups ?? [], candidateUsers ?? [], expectedOutputs);
        var end = new EndEvent("end1");

        var activities = new List<Activity> { start, userTask, end };
        var flows = new List<SequenceFlow>
        {
            new("seq1", start, userTask),
            new("seq2", userTask, end)
        };

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = activities,
            SequenceFlows = flows,
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        // Complete start event to spawn user task
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions([
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(userTask)])
        ]);

        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "userTask1");
        execution.MarkExecuting(taskEntry.ActivityInstanceId);

        // Process the RegisterUserTaskCommand to populate state.UserTasks
        execution.ProcessCommands([new RegisterUserTaskCommand(
            assignee, candidateGroups ?? [], candidateUsers ?? [], expectedOutputs)], taskEntry.ActivityInstanceId);

        execution.ClearUncommittedEvents();
        return (execution, state, taskEntry);
    }

    // --- ProcessCommands: RegisterUserTaskCommand ---

    [TestMethod]
    public void ProcessCommands_RegisterUserTaskCommand_ShouldEmitUserTaskRegistered_AndReturnRegisterEffect()
    {
        // Arrange
        var (execution, state, taskEntry) = CreateWithExecutingUserTask(
            assignee: "alice",
            candidateGroups: ["managers"],
            candidateUsers: ["bob"],
            expectedOutputs: ["approval"]);

        // Events were cleared in helper; re-verify by calling ProcessCommands again on a fresh scenario
        var start = new StartEvent("start1");
        var userTask = new UserTask("userTask1", "alice", ["managers"], ["bob"], ["approval"]);
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, userTask, end],
            SequenceFlows = [new("seq1", start, userTask), new("seq2", userTask, end)],
            ProcessDefinitionId = "pd1"
        };
        var freshState = new WorkflowInstanceState();
        var freshExecution = new WorkflowExecution(freshState, definition);
        freshExecution.Start();

        var startEntry = freshState.Entries.First();
        freshExecution.MarkExecuting(startEntry.ActivityInstanceId);
        freshExecution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        freshExecution.ResolveTransitions([
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(userTask)])
        ]);

        var freshTaskEntry = freshState.GetActiveActivities().First(e => e.ActivityId == "userTask1");
        freshExecution.MarkExecuting(freshTaskEntry.ActivityInstanceId);
        freshExecution.ClearUncommittedEvents();

        // Act
        var effects = freshExecution.ProcessCommands(
            [new RegisterUserTaskCommand("alice", ["managers"], ["bob"], ["approval"])],
            freshTaskEntry.ActivityInstanceId);

        // Assert: UserTaskRegistered event emitted
        var events = freshExecution.GetUncommittedEvents();
        var registered = events.OfType<UserTaskRegistered>().Single();
        Assert.AreEqual(freshTaskEntry.ActivityInstanceId, registered.ActivityInstanceId);
        Assert.AreEqual("alice", registered.Assignee);
        CollectionAssert.AreEqual(new[] { "managers" }, registered.CandidateGroups.ToList());
        CollectionAssert.AreEqual(new[] { "bob" }, registered.CandidateUsers.ToList());
        CollectionAssert.AreEqual(new[] { "approval" }, registered.ExpectedOutputVariables!.ToList());

        // Assert: RegisterUserTaskEffect returned
        var registerEffect = effects.OfType<RegisterUserTaskEffect>().Single();
        Assert.AreEqual(freshState.Id, registerEffect.WorkflowInstanceId);
        Assert.AreEqual(freshTaskEntry.ActivityInstanceId, registerEffect.ActivityInstanceId);
        Assert.AreEqual("userTask1", registerEffect.ActivityId);
        Assert.AreEqual("alice", registerEffect.Assignee);

        // Assert: state.UserTasks populated
        Assert.IsTrue(freshState.UserTasks.ContainsKey(freshTaskEntry.ActivityInstanceId));
        var metadata = freshState.UserTasks[freshTaskEntry.ActivityInstanceId];
        Assert.AreEqual("alice", metadata.Assignee);
        Assert.AreEqual(UserTaskLifecycleState.Created, metadata.TaskState);
    }

    // --- ClaimUserTask ---

    [TestMethod]
    public void ClaimUserTask_ShouldEmitUserTaskClaimed_AndReturnUpdateClaimEffect()
    {
        // Arrange
        var (execution, state, taskEntry) = CreateWithExecutingUserTask(assignee: "alice");

        // Act
        var effects = execution.ClaimUserTask(taskEntry.ActivityInstanceId, "alice");

        // Assert
        var events = execution.GetUncommittedEvents();
        var claimed = events.OfType<UserTaskClaimed>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, claimed.ActivityInstanceId);
        Assert.AreEqual("alice", claimed.UserId);

        var claimEffect = effects.OfType<UpdateUserTaskClaimEffect>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, claimEffect.ActivityInstanceId);
        Assert.AreEqual("alice", claimEffect.ClaimedBy);
        Assert.AreEqual(UserTaskLifecycleState.Claimed, claimEffect.TaskState);
    }

    [TestMethod]
    public void ClaimUserTask_WrongAssignee_ShouldThrowBadRequest()
    {
        // Arrange
        var (execution, state, taskEntry) = CreateWithExecutingUserTask(assignee: "alice");

        // Act & Assert
        Assert.ThrowsExactly<BadRequestActivityException>(
            () => execution.ClaimUserTask(taskEntry.ActivityInstanceId, "bob"));
    }

    [TestMethod]
    public void ClaimUserTask_NotInCandidateUsers_ShouldThrowBadRequest()
    {
        // Arrange
        var (execution, state, taskEntry) = CreateWithExecutingUserTask(
            assignee: null,
            candidateUsers: ["alice", "carol"]);

        // Act & Assert
        Assert.ThrowsExactly<BadRequestActivityException>(
            () => execution.ClaimUserTask(taskEntry.ActivityInstanceId, "bob"));
    }

    // --- UnclaimUserTask ---

    [TestMethod]
    public void UnclaimUserTask_ShouldEmitUserTaskUnclaimed_AndReturnUpdateClaimEffectWithNull()
    {
        // Arrange
        var (execution, state, taskEntry) = CreateWithExecutingUserTask(assignee: "alice");
        execution.ClaimUserTask(taskEntry.ActivityInstanceId, "alice");
        execution.ClearUncommittedEvents();

        // Act
        var effects = execution.UnclaimUserTask(taskEntry.ActivityInstanceId);

        // Assert
        var events = execution.GetUncommittedEvents();
        var unclaimed = events.OfType<UserTaskUnclaimed>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, unclaimed.ActivityInstanceId);

        var claimEffect = effects.OfType<UpdateUserTaskClaimEffect>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, claimEffect.ActivityInstanceId);
        Assert.IsNull(claimEffect.ClaimedBy);
        Assert.AreEqual(UserTaskLifecycleState.Created, claimEffect.TaskState);
    }

    // --- CompleteUserTask ---

    [TestMethod]
    public void CompleteUserTask_ShouldEmitActivityCompleted_AndUserTaskUnregistered_AndReturnUnregisterEffect()
    {
        // Arrange
        var (execution, state, taskEntry) = CreateWithExecutingUserTask(assignee: "alice");
        execution.ClaimUserTask(taskEntry.ActivityInstanceId, "alice");
        execution.ClearUncommittedEvents();

        // Act
        var effects = execution.CompleteUserTask(taskEntry.ActivityInstanceId, "alice", new ExpandoObject());

        // Assert: ActivityCompleted event
        var events = execution.GetUncommittedEvents();
        var completed = events.OfType<ActivityCompleted>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, completed.ActivityInstanceId);

        // Assert: UserTaskUnregistered event
        var unregistered = events.OfType<UserTaskUnregistered>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, unregistered.ActivityInstanceId);

        // Assert: CompleteUserTaskPersistenceEffect returned
        var completeEffect = effects.OfType<CompleteUserTaskPersistenceEffect>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, completeEffect.ActivityInstanceId);
    }

    [TestMethod]
    public void CompleteUserTask_NotClaimed_ShouldThrowBadRequest()
    {
        // Arrange
        var (execution, state, taskEntry) = CreateWithExecutingUserTask(assignee: "alice");
        // Task is in Created state, not claimed

        // Act & Assert
        Assert.ThrowsExactly<BadRequestActivityException>(
            () => execution.CompleteUserTask(taskEntry.ActivityInstanceId, "alice", new ExpandoObject()));
    }

    [TestMethod]
    public void CompleteUserTask_WrongUser_ShouldThrowBadRequest()
    {
        // Arrange
        var (execution, state, taskEntry) = CreateWithExecutingUserTask(assignee: "alice");
        execution.ClaimUserTask(taskEntry.ActivityInstanceId, "alice");
        execution.ClearUncommittedEvents();

        // Act & Assert
        Assert.ThrowsExactly<BadRequestActivityException>(
            () => execution.CompleteUserTask(taskEntry.ActivityInstanceId, "bob", new ExpandoObject()));
    }

    [TestMethod]
    public void CompleteUserTask_MissingExpectedOutputVariables_ShouldThrowBadRequest()
    {
        // Arrange
        var (execution, state, taskEntry) = CreateWithExecutingUserTask(
            assignee: "alice",
            expectedOutputs: ["approvalDecision", "comments"]);
        execution.ClaimUserTask(taskEntry.ActivityInstanceId, "alice");
        execution.ClearUncommittedEvents();

        var incompleteVars = new ExpandoObject();
        ((IDictionary<string, object?>)incompleteVars)["approvalDecision"] = "approved";
        // "comments" is missing

        // Act & Assert
        Assert.ThrowsExactly<BadRequestActivityException>(
            () => execution.CompleteUserTask(taskEntry.ActivityInstanceId, "alice", incompleteVars));
    }

    // --- CompleteActivity guard ---

    [TestMethod]
    public void CompleteActivity_OnUserTask_ShouldThrowInvalidOperation()
    {
        // Arrange
        var (execution, state, taskEntry) = CreateWithExecutingUserTask(assignee: "alice");

        // Act & Assert: CompleteActivity must not be used for user tasks
        Assert.ThrowsExactly<InvalidOperationException>(
            () => execution.CompleteActivity("userTask1", taskEntry.ActivityInstanceId, new ExpandoObject()));
    }
}
