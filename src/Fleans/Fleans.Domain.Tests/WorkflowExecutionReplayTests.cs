using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>
/// The journal is the source of truth: replaying the events a live run emitted must rebuild the
/// same state — ids, flags and timestamps — at any later time.
/// </summary>
[TestClass]
public class WorkflowExecutionReplayTests
{
    private static readonly StartEvent Start = new("start1");
    private static readonly TaskActivity Task1 = new("task1");
    private static readonly EndEvent End = new("end1");

    private static (WorkflowExecution Execution, WorkflowInstanceState State, ActivityInstanceEntry TaskEntry) RunToExecutingTask()
    {
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf-replay",
            ProcessDefinitionId = "pd-replay",
            Activities = [Start, Task1, End],
            SequenceFlows = [new SequenceFlow("seq1", Start, Task1), new SequenceFlow("seq2", Task1, End)]
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start(businessId: "order-1");
        execution.MarkExecutionStarted();

        var startEntry = state.Entries.Single();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
            [new CompletedActivityTransitions(startEntry.ActivityInstanceId, Start.ActivityId, [new ActivityTransition(Task1)])]);

        var taskEntry = state.GetActiveActivities().Single(e => e.ActivityId == Task1.ActivityId);
        execution.MarkExecuting(taskEntry.ActivityInstanceId);
        return (execution, state, taskEntry);
    }

    private static WorkflowInstanceState Replay(IEnumerable<WorkflowEvent> events)
    {
        var state = new WorkflowInstanceState();
        var replay = new WorkflowExecution(state);
        foreach (var e in events)
            replay.ReplayEvent(e);
        return state;
    }

    private static void AssertSameState(WorkflowInstanceState expected, WorkflowInstanceState actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.BusinessId, actual.BusinessId);
        Assert.AreEqual(expected.IsStarted, actual.IsStarted);
        Assert.AreEqual(expected.IsCompleted, actual.IsCompleted);
        Assert.AreEqual(expected.IsFailed, actual.IsFailed);
        Assert.AreEqual(expected.CreatedAt, actual.CreatedAt, "CreatedAt");
        Assert.AreEqual(expected.ExecutionStartedAt, actual.ExecutionStartedAt, "ExecutionStartedAt");
        Assert.AreEqual(expected.CompletedAt, actual.CompletedAt, "CompletedAt");

        Assert.HasCount(expected.Entries.Count, actual.Entries);
        foreach (var e in expected.Entries)
        {
            var a = actual.GetEntry(e.ActivityInstanceId);
            Assert.AreEqual(e.ActivityId, a.ActivityId);
            Assert.AreEqual(e.IsExecuting, a.IsExecuting, $"{e.ActivityId}.IsExecuting");
            Assert.AreEqual(e.IsCompleted, a.IsCompleted, $"{e.ActivityId}.IsCompleted");
            Assert.AreEqual(e.ErrorCode, a.ErrorCode, $"{e.ActivityId}.ErrorCode");
            Assert.AreEqual(e.CreatedAt, a.CreatedAt, $"{e.ActivityId}.CreatedAt");
            Assert.AreEqual(e.ExecutionStartedAt, a.ExecutionStartedAt, $"{e.ActivityId}.ExecutionStartedAt");
            Assert.AreEqual(e.CompletedAt, a.CompletedAt, $"{e.ActivityId}.CompletedAt");
        }

        CollectionAssert.AreEquivalent(
            expected.VariableStates.Select(v => v.Id).ToList(),
            actual.VariableStates.Select(v => v.Id).ToList());
    }

    [TestMethod]
    public void Emit_StampsOccurredAtOnEveryEvent()
    {
        var before = DateTimeOffset.UtcNow;

        var (execution, _, _) = RunToExecutingTask();

        var events = execution.GetUncommittedEvents();
        Assert.IsNotEmpty(events);
        foreach (var e in events)
            Assert.IsGreaterThanOrEqualTo(before, e.OccurredAt, $"{e.GetType().Name}.OccurredAt");
    }

    [TestMethod]
    public void Replay_AfterCompletion_RebuildsIdenticalState()
    {
        // Arrange
        var (execution, liveState, taskEntry) = RunToExecutingTask();
        dynamic vars = new ExpandoObject();
        vars.approved = true;
        execution.CompleteActivity(Task1.ActivityId, taskEntry.ActivityInstanceId, (ExpandoObject)vars);
        var events = execution.GetUncommittedEvents().ToList();

        // Act — later, so a handler that reads the clock would record a different time
        Thread.Sleep(20);
        var replayed = Replay(events);

        // Assert
        AssertSameState(liveState, replayed);
        var completed = replayed.GetEntry(taskEntry.ActivityInstanceId);
        Assert.IsTrue(completed.IsCompleted);
        Assert.AreEqual(true, replayed.GetVariable(completed.VariablesId, "approved"));
    }

    [TestMethod]
    public void Replay_AfterFailure_RebuildsIdenticalState()
    {
        // Arrange
        var (execution, liveState, taskEntry) = RunToExecutingTask();
        execution.FailActivity(Task1.ActivityId, taskEntry.ActivityInstanceId, new Exception("boom"));
        var events = execution.GetUncommittedEvents().ToList();

        // Act
        Thread.Sleep(20);
        var replayed = Replay(events);

        // Assert
        AssertSameState(liveState, replayed);
        var failed = replayed.GetEntry(taskEntry.ActivityInstanceId);
        Assert.IsTrue(failed.IsCompleted);
        Assert.IsNotNull(failed.ErrorCode);
    }
}
