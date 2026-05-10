using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class NestedTxPhaseBDomainTests
{
    private static (WorkflowExecution execution, WorkflowInstanceState state) CreateExecution(
        bool withInnerTx = false)
    {
        var cancelOuter = new CancelEndEvent("cancel-outer");
        var outerTxActivities = withInnerTx
            ? new List<Activity> { cancelOuter, new Transaction("inner-tx") { Activities = [new CancelEndEvent("cancel-inner"), new ScriptTask("handler-act", "return 1;")] } }
            : new List<Activity> { cancelOuter };
        var outerTx = new Transaction("outer-tx") { Activities = outerTxActivities };
        var start = new StartEvent("start");
        var end = new EndEvent("end");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "nested-tx-b-test",
            ProcessDefinitionId = "pd1",
            Activities = [start, outerTx, end],
            SequenceFlows = [new SequenceFlow("s1", start, outerTx), new SequenceFlow("s2", outerTx, end)]
        };

        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();
        execution.ClearUncommittedEvents();
        return (execution, state);
    }

    // Scenario B: Inner TX fires CancelEndEvent; outer continues with no CompensationWalkAborted
    [TestMethod]
    public void NestedTx_InnerCancels_OuterContinues_ScenarioB()
    {
        var (execution, state) = CreateExecution(withInnerTx: true);
        var wfId = state.Id;
        var outerTxId = Guid.NewGuid();
        var innerTxId = Guid.NewGuid();
        var cancelInnerId = Guid.NewGuid();

        // Outer TX active; inner TX has a completed CancelEndEvent (inner TX cancelling itself)
        var outerTxEntry = new ActivityInstanceEntry(outerTxId, "outer-tx", wfId);
        var innerTxEntry = new ActivityInstanceEntry(innerTxId, "inner-tx", wfId, scopeId: outerTxId);
        var cancelInnerEntry = new ActivityInstanceEntry(cancelInnerId, "cancel-inner", wfId, scopeId: innerTxId);
        state.AddEntries([outerTxEntry, innerTxEntry, cancelInnerEntry]);
        state.CompleteEntries([cancelInnerEntry]);

        // Act: outer TX has NO CancelEndEvent — loop skips the outer TX
        execution.InitiateTransactionCancelFlowIfNeeded();

        var events = execution.GetUncommittedEvents();

        // The inner TX cancel is handled separately; this loop only triggers on outer TX CancelEndEvent
        // Since outer TX has no completed CancelEndEvent, nothing fires for the outer TX
        Assert.IsFalse(events.OfType<CompensationWalkAborted>().Any(), "No walk to abort");
        Assert.IsFalse(events.OfType<TransactionOutcomeSet>()
            .Any(e => e.TransactionInstanceId == outerTxId), "Outer TX outcome not set");
    }

    // Scenario C: Outer TX fires CancelEndEvent; inner TX completed (no active walk)
    [TestMethod]
    public void NestedTx_OuterCancels_NoInnerWalk_ScenarioC()
    {
        var (execution, state) = CreateExecution();
        var wfId = state.Id;
        var outerTxId = Guid.NewGuid();
        var cancelOuterId = Guid.NewGuid();

        var outerTxEntry = new ActivityInstanceEntry(outerTxId, "outer-tx", wfId);
        var cancelEntry = new ActivityInstanceEntry(cancelOuterId, "cancel-outer", wfId, scopeId: outerTxId);
        state.AddEntries([outerTxEntry, cancelEntry]);
        state.CompleteEntries([cancelEntry]);

        // Act
        execution.InitiateTransactionCancelFlowIfNeeded();

        var events = execution.GetUncommittedEvents();

        Assert.IsFalse(events.OfType<CompensationWalkAborted>().Any(), "No walk to abort");
        Assert.IsTrue(events.OfType<TransactionOutcomeSet>().Any(e =>
            e.TransactionInstanceId == outerTxId && e.Outcome == TransactionOutcome.Cancelled),
            "Outer TX outcome set to Cancelled");
        Assert.IsTrue(events.OfType<CompensationWalkStarted>().Any(),
            "Outer compensation walk started");
    }

    // Scenario D: Outer TX fires CancelEndEvent; inner TX completed + inner walk completed
    [TestMethod]
    public void NestedTx_OuterCancels_InnerWalkCompleted_ScenarioD()
    {
        var (execution, state) = CreateExecution(withInnerTx: true);
        var wfId = state.Id;
        var outerTxId = Guid.NewGuid();
        var innerTxId = Guid.NewGuid();
        var cancelOuterId = Guid.NewGuid();

        var outerTxEntry = new ActivityInstanceEntry(outerTxId, "outer-tx", wfId);
        var innerTxEntry = new ActivityInstanceEntry(innerTxId, "inner-tx", wfId, scopeId: outerTxId);
        var cancelEntry = new ActivityInstanceEntry(cancelOuterId, "cancel-outer", wfId, scopeId: outerTxId);
        state.AddEntries([outerTxEntry, innerTxEntry, cancelEntry]);
        state.CompleteEntries([cancelEntry, innerTxEntry]);
        // No ActiveCompensationWalks entry for inner TX — its walk already completed

        // Act
        execution.InitiateTransactionCancelFlowIfNeeded();

        var events = execution.GetUncommittedEvents();

        Assert.IsFalse(events.OfType<CompensationWalkAborted>().Any(),
            "No walk to abort — inner walk already done");
        Assert.IsTrue(events.OfType<TransactionOutcomeSet>().Any(e =>
            e.TransactionInstanceId == outerTxId && e.Outcome == TransactionOutcome.Cancelled),
            "Outer TX outcome set to Cancelled");
        Assert.IsTrue(events.OfType<CompensationWalkStarted>().Any(),
            "Outer compensation walk started");
    }

    // Scenario E: Outer TX fires CancelEndEvent; inner TX mid-walk (handler running)
    [TestMethod]
    public void NestedTx_OuterCancels_InnerWalkMidFlight_ScenarioE()
    {
        var (execution, state) = CreateExecution(withInnerTx: true);
        var wfId = state.Id;
        var outerTxId = Guid.NewGuid();
        var innerTxId = Guid.NewGuid();
        var cancelOuterId = Guid.NewGuid();
        var handlerId = Guid.NewGuid();

        var outerTxEntry = new ActivityInstanceEntry(outerTxId, "outer-tx", wfId);
        var innerTxEntry = new ActivityInstanceEntry(innerTxId, "inner-tx", wfId, scopeId: outerTxId);
        var cancelEntry = new ActivityInstanceEntry(cancelOuterId, "cancel-outer", wfId, scopeId: outerTxId);
        // Handler entry is active (mid-flight), scoped to inner TX
        var handlerEntry = new ActivityInstanceEntry(handlerId, "handler-act", wfId, scopeId: innerTxId);

        state.AddEntries([outerTxEntry, innerTxEntry, cancelEntry, handlerEntry]);
        state.CompleteEntries([cancelEntry, innerTxEntry]);

        // Set up inner TX compensation walk mid-flight with handler running
        var thrower = Guid.NewGuid();
        state.StartCompensationWalk(innerTxId, new CompensationWalkState(innerTxId, null, thrower));
        state.SetCompensationHandlerInstanceId(innerTxId, handlerId);

        // Act
        execution.InitiateTransactionCancelFlowIfNeeded();

        var events = execution.GetUncommittedEvents();

        // CompensationWalkAborted emitted for inner TX scope
        var aborted = events.OfType<CompensationWalkAborted>().ToList();
        Assert.AreEqual(1, aborted.Count, "Exactly one walk aborted");
        Assert.AreEqual(innerTxId, aborted[0].ScopeId, "Inner TX walk aborted");
        Assert.AreEqual("OuterTransactionCancellation", aborted[0].Reason);

        // In-flight handler activity entry is cancelled
        Assert.IsTrue(events.OfType<ActivityCancelled>()
            .Any(e => e.ActivityInstanceId == handlerId),
            "In-flight handler cancelled");

        // Outer TX outcome set to Cancelled
        Assert.IsTrue(events.OfType<TransactionOutcomeSet>().Any(e =>
            e.TransactionInstanceId == outerTxId && e.Outcome == TransactionOutcome.Cancelled),
            "Outer TX outcome set to Cancelled");

        // Outer compensation walk started
        Assert.IsTrue(events.OfType<CompensationWalkStarted>().Any(),
            "Outer compensation walk started");

        // Inner TX walk cleared from state (ClearCompensationWalk was applied)
        Assert.IsFalse(state.ActiveCompensationWalks.ContainsKey(innerTxId),
            "Inner TX walk no longer active");
    }
}
