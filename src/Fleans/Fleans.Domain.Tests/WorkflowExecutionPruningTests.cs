using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>
/// What <see cref="WorkflowExecution.PruneFinishedHistory"/> removes and what it must keep (#1050).
/// States are built by replaying events, the way a real instance's state comes about.
/// </summary>
[TestClass]
public class WorkflowExecutionPruningTests
{
    private static readonly Guid RootScope = Guid.NewGuid();

    private readonly WorkflowInstanceState _state = new();
    private readonly WorkflowExecution _execution;

    public WorkflowExecutionPruningTests()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        var inner = new TaskActivity("inner");
        var sub = new SubProcess("sub")
        {
            Activities = [inner],
            SequenceFlows = []
        };
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            ProcessDefinitionId = "wf:1",
            Activities = [start, task, end, sub],
            SequenceFlows = [new SequenceFlow("f1", start, task), new SequenceFlow("f2", task, end)]
        };
        _execution = new WorkflowExecution(_state, definition);
        _execution.ReplayEvent(new WorkflowStarted(Guid.NewGuid(), "wf:1", RootScope));
    }

    private Guid Spawn(string activityId, Guid? scopeId = null, Guid? tokenId = null)
    {
        var id = Guid.NewGuid();
        _execution.ReplayEvent(new ActivitySpawned(id, activityId, activityId, RootScope, scopeId, null, tokenId));
        return id;
    }

    private void Complete(Guid id) => _execution.ReplayEvent(new ActivityCompleted(id, RootScope, new ExpandoObject()));

    private Guid SpawnCompleted(string activityId, Guid? scopeId = null, Guid? tokenId = null)
    {
        var id = Spawn(activityId, scopeId, tokenId);
        Complete(id);
        return id;
    }

    [TestMethod]
    public void Prune_RemovesFinishedEntries_AndKeepsTheActiveOnes()
    {
        var finished = SpawnCompleted("task");
        var active = Spawn("task");

        var (entries, _) = _execution.PruneFinishedHistory();

        Assert.AreEqual(1, entries);
        Assert.IsNull(_state.FindEntry(finished));
        Assert.IsTrue(_state.HasActiveEntry(active));
        Assert.IsTrue(_state.HasPrunedHistory);
    }

    [TestMethod]
    public void Prune_NothingToRemove_LeavesTheHistoryFlagUnset()
    {
        Spawn("task");

        var removed = _execution.PruneFinishedHistory();

        Assert.AreEqual((0, 0), removed);
        Assert.IsFalse(_state.HasPrunedHistory);
    }

    [TestMethod]
    public void Prune_KeepsFailedEntries()
    {
        var failed = Spawn("task");
        _execution.ReplayEvent(new ActivityFailed(failed, "500", "boom"));

        _execution.PruneFinishedHistory();

        Assert.AreEqual("500", _state.GetEntry(failed).ErrorCode);
    }

    [TestMethod]
    public void Prune_KeepsCompletedRootEndEvents()
    {
        var end = SpawnCompleted("end");

        _execution.PruneFinishedHistory();

        Assert.IsNotNull(_state.FindEntry(end));
    }

    [TestMethod]
    public void Prune_RemovesCancelledEntries()
    {
        var cancelled = Spawn("task");
        _execution.ReplayEvent(new ActivityCancelled(cancelled, "interrupted"));

        _execution.PruneFinishedHistory();

        Assert.IsNull(_state.FindEntry(cancelled));
    }

    [TestMethod]
    public void Prune_KeepsEntriesInsideAnOpenScope_AtAnyDepth()
    {
        var outer = Spawn("sub");
        var nested = SpawnCompleted("sub", scopeId: outer);
        var child = SpawnCompleted("inner", scopeId: nested);

        _execution.PruneFinishedHistory();

        Assert.IsNotNull(_state.FindEntry(nested));
        Assert.IsNotNull(_state.FindEntry(child), "A descendant of an open scope stays, through a closed scope");
    }

    [TestMethod]
    public void Prune_RemovesAClosedScope_WithItsChildren()
    {
        var host = Spawn("sub");
        var child = SpawnCompleted("inner", scopeId: host);
        Complete(host);

        var (entries, _) = _execution.PruneFinishedHistory();

        Assert.AreEqual(2, entries);
        Assert.IsNull(_state.FindEntry(host));
        Assert.IsNull(_state.FindEntry(child));
    }

    [TestMethod]
    public void Prune_KeepsAnOpenForksEntries_UntilTheForkCloses()
    {
        var token = Guid.NewGuid();
        var fork = SpawnCompleted("task");
        _execution.ReplayEvent(new GatewayForkCreated(fork, null));
        _execution.ReplayEvent(new GatewayForkTokenAdded(fork, token));
        var branch = SpawnCompleted("task", tokenId: token);

        _execution.PruneFinishedHistory();

        Assert.IsNotNull(_state.FindEntry(fork));
        Assert.IsNotNull(_state.FindEntry(branch));

        _execution.ReplayEvent(new GatewayForkRemoved(fork));
        _execution.PruneFinishedHistory();

        Assert.IsNull(_state.FindEntry(fork));
        Assert.IsNull(_state.FindEntry(branch));
    }

    [TestMethod]
    public void Prune_KeepsAComplexJoinsFirstEntry()
    {
        var first = SpawnCompleted("task");
        _execution.ReplayEvent(new ComplexGatewayJoinStateCreated("join", first, "true", _state.Id));

        _execution.PruneFinishedHistory();

        Assert.IsNotNull(_state.FindEntry(first));
    }

    [TestMethod]
    public void Prune_KeepsARunningCompensationWalksThrowerAndHandler()
    {
        var thrower = SpawnCompleted("task");
        var handler = Spawn("task");
        _execution.ReplayEvent(new CompensationWalkStarted(null, null, 1, thrower));
        _execution.ReplayEvent(new CompensationHandlerSpawned(handler, "task", "task", null));
        Complete(handler);

        _execution.PruneFinishedHistory();

        Assert.IsNotNull(_state.FindEntry(thrower));
        Assert.IsNotNull(_state.FindEntry(handler));
    }

    [TestMethod]
    public void Prune_KeepsAScopeWhoseCompensationWalkOutlivesItsHost()
    {
        // A cancelled Transaction completes while its walk still has handlers to run
        var host = SpawnCompleted("task");
        var child = SpawnCompleted("task", scopeId: host);
        var thrower = SpawnCompleted("task", scopeId: host);
        _execution.ReplayEvent(new CompensationWalkStarted(host, null, 1, thrower));

        _execution.PruneFinishedHistory();

        Assert.IsNotNull(_state.FindEntry(host));
        Assert.IsNotNull(_state.FindEntry(child));

        _execution.ReplayEvent(new CompensationWalkCompleted(host));
        _execution.PruneFinishedHistory();

        Assert.IsNull(_state.FindEntry(host));
        Assert.IsNull(_state.FindEntry(child));
    }

    [TestMethod]
    public void Prune_RemovesConditionResults_OnlyOfGatewaysThatFinishedWithoutError()
    {
        var finished = SpawnCompleted("task");
        var running = Spawn("task");
        var failed = Spawn("task");
        foreach (var gateway in new[] { finished, running, failed })
            _execution.ReplayEvent(new ConditionSequencesAdded(gateway, ["flow"]));
        _execution.ReplayEvent(new ActivityFailed(failed, "500", "boom"));

        var (_, conditionResults) = _execution.PruneFinishedHistory();

        Assert.AreEqual(1, conditionResults);
        CollectionAssert.AreEquivalent(
            new[] { running, failed },
            _state.ConditionSequenceStates.Select(c => c.GatewayActivityInstanceId).ToList());
    }

    [TestMethod]
    public void Prune_RemovesConditionResults_OfAFinishedGatewayStillInsideAnOpenScope()
    {
        var host = Spawn("sub");
        var gateway = SpawnCompleted("inner", scopeId: host);
        _execution.ReplayEvent(new ConditionSequencesAdded(gateway, ["flow"]));

        var (_, conditionResults) = _execution.PruneFinishedHistory();

        Assert.AreEqual(1, conditionResults);
        Assert.IsNotNull(_state.FindEntry(gateway));
    }

    [TestMethod]
    public void Prune_EntriesAddedAfterwards_AreFoundAndCompleteNormally()
    {
        SpawnCompleted("task");
        _execution.PruneFinishedHistory();

        var next = Spawn("task");
        Complete(next);

        Assert.IsTrue(_state.GetEntry(next).IsCompleted);
        Assert.IsFalse(_state.HasActiveEntry(next));
        Assert.HasCount(1, _state.Entries);
    }

    [TestMethod]
    public void CallbacksForAPrunedEntry_AreStale()
    {
        var pruned = SpawnCompleted("task");
        _execution.PruneFinishedHistory();

        var marked = _execution.MarkCompleted(pruned, new ExpandoObject());
        var completed = _execution.CompleteActivity("task", pruned, new ExpandoObject());
        var failed = _execution.FailActivity("task", pruned, new InvalidOperationException("late"));

        Assert.HasCount(0, marked);
        Assert.HasCount(0, completed);
        Assert.HasCount(0, failed);
        Assert.HasCount(0, _execution.GetUncommittedEvents());
    }

    [TestMethod]
    public void Replay_TailOverAPrunedState_MatchesAFullReplay_ExceptForThePrunedEntries()
    {
        // Arrange: the same events applied with a prune in the middle, as a snapshot does, and without.
        var running = Guid.NewGuid();
        var finished = Guid.NewGuid();
        List<WorkflowEvent> before =
        [
            new WorkflowStarted(Guid.NewGuid(), "wf:1", RootScope),
            new ActivitySpawned(finished, "task", "TaskActivity", RootScope, null, null, null),
            new ActivityCompleted(finished, RootScope, new ExpandoObject()),
            new ActivitySpawned(running, "task", "TaskActivity", RootScope, null, null, null),
        ];
        List<WorkflowEvent> tail =
        [
            new ActivityExecutionStarted(running),
            new ActivityCompleted(running, RootScope, new ExpandoObject()),
            new WorkflowCompleted(),
        ];

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            ProcessDefinitionId = "wf:1",
            Activities = [new TaskActivity("task")],
            SequenceFlows = []
        };
        var pruned = new WorkflowInstanceState();
        var prunedExecution = new WorkflowExecution(pruned, definition);
        before.ForEach(prunedExecution.ReplayEvent);
        prunedExecution.PruneFinishedHistory();
        tail.ForEach(prunedExecution.ReplayEvent);

        var full = new WorkflowInstanceState();
        var fullExecution = new WorkflowExecution(full, definition);
        before.Concat(tail).ToList().ForEach(fullExecution.ReplayEvent);

        // Assert
        Assert.HasCount(2, full.Entries);
        Assert.IsFalse(full.HasPrunedHistory);
        Assert.AreEqual(running, pruned.Entries.Single().ActivityInstanceId);
        Assert.IsTrue(pruned.HasPrunedHistory);
        Assert.AreEqual(WorkflowInstanceRow.From(full), WorkflowInstanceRow.From(pruned));
    }
}
