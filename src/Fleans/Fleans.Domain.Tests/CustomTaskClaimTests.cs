using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Effects;
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

    [TestMethod]
    public void Interrupting_boundary_timer_cancels_the_running_dispatch_on_its_worker()
    {
        // Arrange — #786: a dispatch holds the task while its worker runs it.
        var (execution, task) = CreateWithExecutingCustomTask(withBoundaryTimer: true);
        var dispatchId = Guid.NewGuid();
        execution.ClaimCustomTask(task.ActivityInstanceId, dispatchId, Lease);

        // Act
        var effects = execution.HandleTimerFired("timeout1", task.ActivityInstanceId);

        // Assert
        Assert.IsTrue(task.IsCancelled);
        var cancel = effects.OfType<CancelCustomTaskEffect>().Single();
        Assert.AreEqual(task.ActivityInstanceId, cancel.ActivityInstanceId);
        Assert.AreEqual(dispatchId, cancel.DispatchId);
    }

    [TestMethod]
    public void Cancelling_the_instance_cancels_the_running_dispatch_on_its_worker()
    {
        // Arrange
        var (execution, task) = CreateWithExecutingCustomTask();
        execution.MarkExecutionStarted();
        var dispatchId = Guid.NewGuid();
        execution.ClaimCustomTask(task.ActivityInstanceId, dispatchId, Lease);

        // Act
        var effects = execution.CancelInstance("operator");

        // Assert
        Assert.IsTrue(task.IsCancelled);
        Assert.AreEqual(dispatchId, effects.OfType<CancelCustomTaskEffect>().Single().DispatchId);
    }

    [TestMethod]
    public void Cancelling_a_task_no_dispatch_holds_cancels_nothing_on_a_worker()
    {
        // Arrange — the last dispatch released its claim (rethrown for redelivery).
        var (execution, task) = CreateWithExecutingCustomTask(withBoundaryTimer: true);
        var dispatchId = Guid.NewGuid();
        execution.ClaimCustomTask(task.ActivityInstanceId, dispatchId, Lease);
        execution.ReleaseCustomTaskClaim(task.ActivityInstanceId, dispatchId);

        // Act
        var effects = execution.HandleTimerFired("timeout1", task.ActivityInstanceId);

        // Assert
        Assert.IsTrue(task.IsCancelled);
        Assert.IsEmpty(effects.OfType<CancelCustomTaskEffect>());
    }

    [TestMethod]
    public void Cancelling_a_deferred_task_cancels_nothing_on_a_worker()
    {
        // Arrange — the worker deferred the task: no dispatch waits on it.
        var (execution, task) = CreateWithExecutingCustomTask(withBoundaryTimer: true);
        execution.ClaimCustomTask(task.ActivityInstanceId, Guid.NewGuid(), lease: null);

        // Act
        var effects = execution.HandleTimerFired("timeout1", task.ActivityInstanceId);

        // Assert
        Assert.IsTrue(task.IsCancelled);
        Assert.IsEmpty(effects.OfType<CancelCustomTaskEffect>());
    }

    private static (WorkflowExecution Execution, ActivityInstanceEntry Task) CreateWithExecutingCustomTask(bool withBoundaryTimer = false)
    {
        var (execution, task, _, _) = CreateWithExecutingCustomTaskAndState(withBoundaryTimer);
        return (execution, task);
    }

    private static (WorkflowExecution Execution, ActivityInstanceEntry Task, WorkflowInstanceState State, WorkflowDefinition Definition)
        CreateWithExecutingCustomTaskAndState(bool withBoundaryTimer = false)
    {
        var start = new StartEvent("start1");
        var customTask = new CustomTaskActivity("ct1", "rest-call");
        var end = new EndEvent("end1");
        List<Activity> activities = [start, customTask, end];
        List<SequenceFlow> flows = [new SequenceFlow("seq1", start, customTask), new SequenceFlow("seq2", customTask, end)];
        if (withBoundaryTimer)
        {
            var timer = new BoundaryTimerEvent("timeout1", "ct1", new TimerDefinition(TimerType.Duration, "PT3S"));
            var timeoutEnd = new EndEvent("endTimeout");
            activities.AddRange([timer, timeoutEnd]);
            flows.Add(new SequenceFlow("seq3", timer, timeoutEnd));
        }
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
