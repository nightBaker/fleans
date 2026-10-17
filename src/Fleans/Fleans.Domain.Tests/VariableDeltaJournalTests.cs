using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>
/// #867: ActivityCompleted / VariablesMerged journal only the variables the merge changes.
/// </summary>
[TestClass]
public class VariableDeltaJournalTests
{
    private static (WorkflowExecution Execution, WorkflowInstanceState State, ActivityInstanceEntry Task) CreateWithExecutingTask()
    {
        var start = new StartEvent("start1");
        var task = new ScriptTask("task1", "return 1;");
        var end = new EndEvent("end1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            ProcessDefinitionId = "pd1",
            Activities = [start, task, end],
            SequenceFlows = [new("seq1", start, task), new("seq2", task, end)],
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1", [new ActivityTransition(task)])
        ]);

        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");
        execution.MarkExecuting(taskEntry.ActivityInstanceId);
        return (execution, state, taskEntry);
    }

    private static ExpandoObject Vars(params (string Key, object? Value)[] entries)
    {
        var result = new ExpandoObject();
        var dict = (IDictionary<string, object?>)result;
        foreach (var (key, value) in entries)
            dict[key] = value;
        return result;
    }

    private static ExpandoObject FullScope(long amount) => Vars(
        ("orderId", "o-1"),
        ("amount", amount),
        ("items", new List<object> { "i1", "i2", "i3" }),
        ("customer", Vars(("name", "Ann"), ("tags", new List<object> { "vip" }))));

    [TestMethod]
    public void MarkCompleted_FullScopeWithOneChange_JournalsOnlyTheChangedVariable()
    {
        // Arrange
        var (execution, state, task) = CreateWithExecutingTask();
        execution.MergeVariables(task.VariablesId, FullScope(449));
        execution.ClearUncommittedEvents();

        // Act — a script returns the whole scope with one value changed and one added
        var returned = FullScope(500);
        ((IDictionary<string, object?>)returned)["stage"] = 2;
        execution.MarkCompleted(task.ActivityInstanceId, returned);

        // Assert
        var completed = execution.GetUncommittedEvents().OfType<ActivityCompleted>().Single();
        var journaled = (IDictionary<string, object?>)completed.Variables;
        CollectionAssert.AreEquivalent(new[] { "amount", "stage" }, journaled.Keys.ToList());
        Assert.AreEqual(500L, journaled["amount"]);

        var scope = (IDictionary<string, object?>)state.GetVariableState(task.VariablesId).Variables;
        Assert.AreEqual(500L, scope["amount"]);
        Assert.AreEqual(2, scope["stage"]);
        Assert.AreEqual("o-1", scope["orderId"]);
    }

    [TestMethod]
    public void MergeVariables_NothingChanged_JournalsEmptyVariables()
    {
        // Arrange
        var (execution, _, task) = CreateWithExecutingTask();
        execution.MergeVariables(task.VariablesId, FullScope(449));
        execution.ClearUncommittedEvents();

        // Act
        execution.MergeVariables(task.VariablesId, FullScope(449));

        // Assert
        var merged = execution.GetUncommittedEvents().OfType<VariablesMerged>().Single();
        Assert.AreEqual(0, ((IDictionary<string, object?>)merged.Variables).Count);
    }

    [TestMethod]
    public void Replay_OfDeltaJournal_RebuildsTheLiveVariables()
    {
        // Arrange
        var (execution, state, task) = CreateWithExecutingTask();
        execution.MergeVariables(task.VariablesId, FullScope(449));
        var returned = FullScope(449);
        ((IDictionary<string, object?>)returned)["items"] = new List<object> { "i1", "i2" };
        execution.MarkCompleted(task.ActivityInstanceId, returned);
        var journal = execution.GetUncommittedEvents().ToList();

        // Act
        var replayed = new WorkflowInstanceState();
        var replay = new WorkflowExecution(replayed);
        foreach (var evt in journal)
            replay.ReplayEvent(evt);

        // Assert — replayed scope equals the live scope, and the task completed
        var live = state.GetVariableState(task.VariablesId).Variables;
        var rebuilt = replayed.GetVariableState(task.VariablesId).Variables;
        Assert.IsTrue(VariableValues.AreEqual(live, rebuilt));
        Assert.IsTrue(replayed.GetEntry(task.ActivityInstanceId).IsCompleted);
    }

    [TestMethod]
    public void AreEqual_ComparesStructurally()
    {
        Assert.IsTrue(VariableValues.AreEqual(FullScope(1), FullScope(1)));
        Assert.IsTrue(VariableValues.AreEqual(null, null));
        Assert.IsFalse(VariableValues.AreEqual(FullScope(1), FullScope(2)));
        Assert.IsFalse(VariableValues.AreEqual(1, 1L), "a different runtime type is a change");
        Assert.IsFalse(VariableValues.AreEqual(new List<object> { "a", "b" }, new List<object> { "b", "a" }));
        Assert.IsFalse(VariableValues.AreEqual(new List<object> { "a" }, new object[] { "a" }));
        Assert.IsFalse(VariableValues.AreEqual(Vars(("a", 1)), Vars(("a", 1), ("b", null))));
        Assert.IsFalse(VariableValues.AreEqual(new object(), new object()), "unknown reference types compare by identity");
    }
}
