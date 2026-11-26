using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class CancelInstanceDomainTests
{
    [TestMethod]
    public void CancelInstance_CancelsEntriesOutsideTheRootPass_AndAbortsCompensationWalks()
    {
        // Arrange — a root task, an entry whose scope host already finished (the root pass,
        // which walks down from root entries, never reaches it), and a running compensation walk.
        var task = new TaskActivity("task");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "cancel-instance",
            ProcessDefinitionId = "pd1",
            Activities = [new StartEvent("start"), task],
            SequenceFlows = []
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();
        execution.MarkExecutionStarted();
        var wfId = state.Id;
        var finishedHost = new ActivityInstanceEntry(Guid.NewGuid(), "sub", wfId);
        var rootTask = new ActivityInstanceEntry(Guid.NewGuid(), "task", wfId);
        var orphan = new ActivityInstanceEntry(Guid.NewGuid(), "task", wfId, scopeId: finishedHost.ActivityInstanceId);
        state.AddEntries([finishedHost, rootTask, orphan]);
        state.CompleteEntries([finishedHost]);
        state.StartCompensationWalk(null, new CompensationWalkState(null, null, rootTask.ActivityInstanceId));
        execution.ClearUncommittedEvents();

        // Act
        execution.CancelInstance("cancelled by test");

        // Assert
        var events = execution.GetUncommittedEvents();
        var cancelled = events.OfType<ActivityCancelled>().Select(e => e.ActivityInstanceId).ToList();
        CollectionAssert.Contains(cancelled, rootTask.ActivityInstanceId);
        CollectionAssert.Contains(cancelled, orphan.ActivityInstanceId);
        Assert.AreEqual(0, state.GetActiveActivities().Count(), "nothing stays active on a cancelled instance");
        Assert.AreEqual(0, state.ActiveCompensationWalks.Count, "the compensation walk is aborted");
        Assert.IsTrue(events.OfType<WorkflowCancelled>().Any());
    }
}
