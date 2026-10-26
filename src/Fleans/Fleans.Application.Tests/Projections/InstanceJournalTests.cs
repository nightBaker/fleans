using System.Dynamic;
using Fleans.Application.QueryModels;
using Fleans.Domain.Events;
using NSubstitute;

namespace Fleans.Application.Tests.Projections;

/// <summary>
/// The admin UI's debug mode: one step per journal event, with the activity it is about, the
/// variable values it changed, and the state as of any version rebuilt by replay.
/// </summary>
[TestClass]
public class InstanceJournalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceId = Guid.NewGuid();
    private static readonly Guid RootScope = Guid.NewGuid();
    private static readonly Guid TaskInstance = Guid.NewGuid();

    private static ExpandoObject Vars(params (string Name, object Value)[] values)
    {
        var vars = new ExpandoObject();
        foreach (var (name, value) in values)
            ((IDictionary<string, object?>)vars)[name] = value;
        return vars;
    }

    private static T At<T>(T evt, int second) where T : WorkflowEvent => evt with { OccurredAt = T0.AddSeconds(second) };

    /// <summary>Start → task spawned → started → completed with <c>amount = 5</c>.</summary>
    private static List<WorkflowEvent> TaskRun(WorkflowEvent outcome) =>
    [
        At(new WorkflowStarted(InstanceId, "process:1", RootScope), 0),
        At(new ExecutionStarted(), 0),
        At(new ActivitySpawned(TaskInstance, "task1", "ScriptTask", RootScope, null, null, null), 1),
        At(new ActivityExecutionStarted(TaskInstance), 1),
        At(outcome, 2),
    ];

    [TestMethod]
    public void Steps_OnePerEvent_WithVersionActivityAndKind()
    {
        // Arrange
        var events = TaskRun(new ActivityCompleted(TaskInstance, RootScope, Vars(("amount", 5))));
        events.Add(At(new WorkflowCompleted(), 3));

        // Act
        var journal = new InstanceJournal(events);

        // Assert
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, journal.Steps.Select(s => s.Version).ToArray());
        CollectionAssert.AreEqual(
            new[] { "WorkflowStarted", "ExecutionStarted", "ActivitySpawned", "ActivityExecutionStarted", "ActivityCompleted", "WorkflowCompleted" },
            journal.Steps.Select(s => s.EventType).ToArray());

        // Events that carry only the instance id are resolved to the BPMN element.
        var started = journal.Steps[3];
        Assert.AreEqual(TaskInstance, started.ActivityInstanceId);
        Assert.AreEqual("task1", started.ActivityId);
        Assert.AreEqual("task1 started", started.Summary);

        var completed = journal.Steps[4];
        Assert.AreEqual("ok", completed.Kind);
        Assert.AreEqual("task1 completed · 1 variable changed", completed.Summary);
        Assert.AreEqual(T0.AddSeconds(2), completed.OccurredAt);
        Assert.IsNull(journal.Steps[0].ActivityId);
    }

    [TestMethod]
    public void Steps_RecordEachVariableValueAddedChangedAndRemoved()
    {
        // Arrange
        var childScope = Guid.NewGuid();
        var events = TaskRun(new ActivityCompleted(TaskInstance, RootScope, Vars(("amount", 5), ("status", "new"))));
        events.Add(At(new VariablesMerged(RootScope, Vars(("amount", 7), ("status", "new"))), 3));
        events.Add(At(new ChildVariableScopeCreated(childScope, RootScope), 4));
        events.Add(At(new VariablesMerged(childScope, Vars(("item", "a"))), 4));
        events.Add(At(new VariableScopesRemoved([childScope]), 5));

        // Act
        var steps = new InstanceJournal(events).Steps;

        // Assert
        Assert.AreEqual(0, steps[3].VariableChanges.Count, "starting the task changes no variable");
        CollectionAssert.AreEqual(
            new[] { new VariableChange(RootScope, "amount", null, "5"), new VariableChange(RootScope, "status", null, "new") },
            steps[4].VariableChanges);
        // Re-merging an unchanged value is not a change.
        CollectionAssert.AreEqual(new[] { new VariableChange(RootScope, "amount", "5", "7") }, steps[5].VariableChanges);
        CollectionAssert.AreEqual(new[] { new VariableChange(childScope, "item", null, "a") }, steps[7].VariableChanges);
        CollectionAssert.AreEqual(new[] { new VariableChange(childScope, "item", "a", null) }, steps[8].VariableChanges);
    }

    [TestMethod]
    public void StateAt_AfterCompletion_RebuildsTheStateAsOfEachVersion()
    {
        // Arrange
        var events = TaskRun(new ActivityCompleted(TaskInstance, RootScope, Vars(("amount", 5))));
        events.Add(At(new WorkflowCompleted(), 3));
        var journal = new InstanceJournal(events);

        // Act
        var beforeAny = journal.StateAt(0);
        var running = journal.StateAt(4);
        var done = journal.StateAt(6);

        // Assert
        Assert.IsFalse(beforeAny.IsStarted);
        Assert.AreEqual(0, beforeAny.StateVersion);

        CollectionAssert.AreEqual(new[] { "task1" }, running.ActiveActivityIds.ToArray());
        Assert.IsTrue(running.ActiveActivities.Single().IsExecuting);
        Assert.AreEqual(0, running.VariableStates.Single().Variables.Count, "amount is set only when the task completes");
        Assert.AreEqual(4, running.StateVersion);

        Assert.IsTrue(done.IsCompleted);
        CollectionAssert.AreEqual(new[] { "task1" }, done.CompletedActivityIds.ToArray());
        Assert.AreEqual(0, done.ActiveActivityIds.Count);
        Assert.AreEqual("5", done.VariableStates.Single().Variables["amount"]);
    }

    [TestMethod]
    public void StateAt_AfterFailure_ShowsTheErrorAtThatStepOnly()
    {
        // Arrange
        var events = TaskRun(new ActivityFailed(TaskInstance, "500", "boom"));
        events.Add(At(new WorkflowFailed("task1", TaskInstance, "500", "boom"), 3));
        var journal = new InstanceJournal(events);

        // Act
        var beforeFailure = journal.StateAt(4);
        var afterFailure = journal.StateAt(5);
        var failedStep = journal.Steps[4];

        // Assert
        Assert.AreEqual("error", failedStep.Kind);
        Assert.AreEqual("task1 failed: [500] boom", failedStep.Summary);
        Assert.AreEqual("error", journal.Steps[5].Kind);
        Assert.AreEqual("task1", journal.Steps[5].ActivityId);

        Assert.IsNull(beforeFailure.ActiveActivities.Single().ErrorState);
        var failed = afterFailure.CompletedActivities.Single();
        Assert.AreEqual("500", failed.ErrorState!.Code);
        Assert.AreEqual("boom", failed.ErrorState.Message);
        Assert.IsTrue(journal.StateAt(6).IsFailed);
        Assert.IsFalse(afterFailure.IsFailed);
    }

    [TestMethod]
    public void PayloadAt_ReturnsTheEventAsJson()
    {
        // Arrange
        var journal = new InstanceJournal(TaskRun(new ActivityCompleted(TaskInstance, RootScope, Vars(("amount", 5)))));

        // Act
        var payload = journal.PayloadAt(5);

        // Assert
        StringAssert.Contains(payload, TaskInstance.ToString());
        StringAssert.Contains(payload, "\"amount\": 5");
    }

    [TestMethod]
    public async Task LoadAsync_ReadsTheWholeJournal()
    {
        // Arrange
        var store = Substitute.For<IEventStore>();
        store.ReadEventsAsync(InstanceId, 0).Returns(TaskRun(new ActivityCompleted(TaskInstance, RootScope, Vars())));

        // Act
        var journal = await InstanceJournal.LoadAsync(store, InstanceId);

        // Assert
        await store.DidNotReceive().ReadSnapshotAsync(Arg.Any<Guid>());
        Assert.AreEqual(5, journal.Steps.Count);
    }
}
