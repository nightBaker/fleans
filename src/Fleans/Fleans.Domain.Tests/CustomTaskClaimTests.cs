using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>A custom task is claimed by one dispatch at a time (#818).</summary>
[TestClass]
public class CustomTaskClaimTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    [TestMethod]
    public void First_claim_is_granted_and_journaled()
    {
        // Arrange
        var (execution, task) = CreateWithExecutingCustomTask();
        var dispatchId = Guid.NewGuid();

        // Act
        var result = execution.ClaimCustomTask(task.ActivityInstanceId, dispatchId, Lease);

        // Assert
        Assert.AreEqual(CustomTaskClaimResult.Granted, result);
        Assert.AreEqual(dispatchId, task.CustomTaskClaimId);
        var claimed = execution.GetUncommittedEvents().OfType<CustomTaskClaimed>().Single();
        Assert.AreEqual(dispatchId, claimed.DispatchId);
        Assert.IsNotNull(claimed.LeaseUntil);
        Assert.IsTrue((claimed.LeaseUntil.Value - (claimed.OccurredAt + Lease)).Duration() < TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public void Claim_of_another_dispatch_is_held_while_the_lease_runs()
    {
        // Arrange
        var (execution, task) = CreateWithExecutingCustomTask();
        var holder = Guid.NewGuid();
        execution.ClaimCustomTask(task.ActivityInstanceId, holder, Lease);
        execution.ClearUncommittedEvents();

        // Act
        var result = execution.ClaimCustomTask(task.ActivityInstanceId, Guid.NewGuid(), Lease);

        // Assert
        Assert.AreEqual(CustomTaskClaimResult.Held, result);
        Assert.AreEqual(holder, task.CustomTaskClaimId);
        Assert.IsEmpty(execution.GetUncommittedEvents());
    }

    [TestMethod]
    public void Same_dispatch_claiming_again_renews_its_lease()
    {
        // Arrange
        var (execution, task) = CreateWithExecutingCustomTask();
        var holder = Guid.NewGuid();
        execution.ClaimCustomTask(task.ActivityInstanceId, holder, TimeSpan.FromSeconds(1));
        var firstLease = task.CustomTaskClaimUntil;

        // Act
        var result = execution.ClaimCustomTask(task.ActivityInstanceId, holder, Lease);

        // Assert
        Assert.AreEqual(CustomTaskClaimResult.Granted, result);
        Assert.IsTrue(task.CustomTaskClaimUntil > firstLease);
    }

    [TestMethod]
    public void Renewal_with_most_of_the_lease_left_journals_nothing()
    {
        // Arrange
        var (execution, task) = CreateWithExecutingCustomTask();
        var holder = Guid.NewGuid();
        execution.ClaimCustomTask(task.ActivityInstanceId, holder, Lease);
        var lease = task.CustomTaskClaimUntil;
        execution.ClearUncommittedEvents();

        // Act
        var result = execution.ClaimCustomTask(task.ActivityInstanceId, holder, Lease);

        // Assert
        Assert.AreEqual(CustomTaskClaimResult.Granted, result);
        Assert.AreEqual(lease, task.CustomTaskClaimUntil);
        Assert.IsEmpty(execution.GetUncommittedEvents());
    }

    [TestMethod]
    public void Claim_whose_lease_ran_out_is_taken_by_the_next_dispatch()
    {
        // Arrange — a holder that died: its lease is already over.
        var (execution, task) = CreateWithExecutingCustomTask();
        execution.ClaimCustomTask(task.ActivityInstanceId, Guid.NewGuid(), TimeSpan.FromSeconds(-1));
        var next = Guid.NewGuid();

        // Act
        var result = execution.ClaimCustomTask(task.ActivityInstanceId, next, Lease);

        // Assert
        Assert.AreEqual(CustomTaskClaimResult.Granted, result);
        Assert.AreEqual(next, task.CustomTaskClaimId);
    }

    [TestMethod]
    public void Released_claim_is_taken_by_the_next_dispatch_at_once()
    {
        // Arrange
        var (execution, task) = CreateWithExecutingCustomTask();
        var holder = Guid.NewGuid();
        execution.ClaimCustomTask(task.ActivityInstanceId, holder, Lease);

        // Act
        execution.ReleaseCustomTaskClaim(task.ActivityInstanceId, holder);
        var result = execution.ClaimCustomTask(task.ActivityInstanceId, Guid.NewGuid(), Lease);

        // Assert
        Assert.AreEqual(CustomTaskClaimResult.Granted, result);
    }

    [TestMethod]
    public void Release_by_a_dispatch_that_does_not_hold_the_claim_is_a_no_op()
    {
        // Arrange
        var (execution, task) = CreateWithExecutingCustomTask();
        var holder = Guid.NewGuid();
        execution.ClaimCustomTask(task.ActivityInstanceId, holder, Lease);
        execution.ClearUncommittedEvents();

        // Act
        execution.ReleaseCustomTaskClaim(task.ActivityInstanceId, Guid.NewGuid());

        // Assert
        Assert.AreEqual(holder, task.CustomTaskClaimId);
        Assert.IsEmpty(execution.GetUncommittedEvents());
    }

    [TestMethod]
    public void Claim_without_a_lease_holds_until_settled_and_answers_deferred()
    {
        // Arrange — the worker deferred the task.
        var (execution, task) = CreateWithExecutingCustomTask();
        execution.ClaimCustomTask(task.ActivityInstanceId, Guid.NewGuid(), lease: null);

        // Act
        var result = execution.ClaimCustomTask(task.ActivityInstanceId, Guid.NewGuid(), Lease);

        // Assert
        Assert.AreEqual(CustomTaskClaimResult.Deferred, result);
        Assert.IsNull(task.CustomTaskClaimUntil);
    }

    [TestMethod]
    public void Claim_of_a_completed_task_answers_finished()
    {
        // Arrange
        var (execution, task) = CreateWithExecutingCustomTask();
        execution.ClaimCustomTask(task.ActivityInstanceId, Guid.NewGuid(), Lease);
        execution.MarkCompleted(task.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Act
        var result = execution.ClaimCustomTask(task.ActivityInstanceId, Guid.NewGuid(), Lease);

        // Assert
        Assert.AreEqual(CustomTaskClaimResult.Finished, result);
        Assert.IsEmpty(execution.GetUncommittedEvents());
    }

    [TestMethod]
    public void Claim_of_a_failed_task_answers_finished()
    {
        // Arrange
        var (execution, task) = CreateWithExecutingCustomTask();
        execution.ClaimCustomTask(task.ActivityInstanceId, Guid.NewGuid(), Lease);
        execution.FailActivity("ct1", task.ActivityInstanceId, new Exception("boom"));

        // Act
        var result = execution.ClaimCustomTask(task.ActivityInstanceId, Guid.NewGuid(), Lease);

        // Assert
        Assert.AreEqual(CustomTaskClaimResult.Finished, result);
    }

    [TestMethod]
    public void Replay_of_two_journaled_claims_keeps_the_first_holder()
    {
        // Arrange — two activations each journaled a claim (the second behind a conflicting append, #845).
        var (_, task, state, definition) = CreateWithExecutingCustomTaskAndState();
        var replay = new WorkflowExecution(state, definition);
        var first = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        // Act
        replay.ReplayEvent(new CustomTaskClaimed(task.ActivityInstanceId, first, at + Lease) { OccurredAt = at });
        replay.ReplayEvent(new CustomTaskClaimed(task.ActivityInstanceId, Guid.NewGuid(), at + Lease) { OccurredAt = at.AddSeconds(1) });

        // Assert
        Assert.AreEqual(first, task.CustomTaskClaimId);
    }

    private static (WorkflowExecution Execution, ActivityInstanceEntry Task) CreateWithExecutingCustomTask()
    {
        var (execution, task, _, _) = CreateWithExecutingCustomTaskAndState();
        return (execution, task);
    }

    private static (WorkflowExecution Execution, ActivityInstanceEntry Task, WorkflowInstanceState State, WorkflowDefinition Definition)
        CreateWithExecutingCustomTaskAndState()
    {
        var start = new StartEvent("start1");
        var customTask = new CustomTaskActivity("ct1", "rest-call");
        var end = new EndEvent("end1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, customTask, end],
            SequenceFlows = [new SequenceFlow("seq1", start, customTask), new SequenceFlow("seq2", customTask, end)],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions([
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(customTask)])
        ]);

        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "ct1");
        execution.MarkExecuting(taskEntry.ActivityInstanceId);
        execution.ClearUncommittedEvents();
        return (execution, taskEntry, state, definition);
    }
}
