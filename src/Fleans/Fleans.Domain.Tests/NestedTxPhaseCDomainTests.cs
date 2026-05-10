using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class NestedTxPhaseCDomainTests
{
    private static (WorkflowExecution execution, WorkflowInstanceState state) CreateExecution()
    {
        var cancelOuter = new CancelEndEvent("cancel-outer");
        var innerTx = new Transaction("inner-tx") { Activities = [new ScriptTask("inner-task", "return 1;")] };
        var outerSiblingTask = new ScriptTask("outer-sibling", "return 1;");
        var outerTx = new Transaction("outer-tx") { Activities = [cancelOuter, innerTx, outerSiblingTask] };
        var start = new StartEvent("start");
        var end = new EndEvent("end");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "nested-tx-c-test",
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

    // Scenario F: Outer TX fires CancelEndEvent, but a descendant TX ended in Hazard.
    // Expected: outer TX outcome is Hazard (not Cancelled), scope children are cancelled,
    // and no compensation walk is started for the outer TX.
    [TestMethod]
    public void NestedTx_OuterCancels_AfterInnerHazard_ScenarioF()
    {
        var (execution, state) = CreateExecution();
        var wfId = state.Id;
        var outerTxId = Guid.NewGuid();
        var innerTxId = Guid.NewGuid();
        var outerSiblingId = Guid.NewGuid();
        var cancelOuterId = Guid.NewGuid();

        // Outer TX is active; it has an active sibling task and a completed CancelEndEvent
        var outerTxEntry = new ActivityInstanceEntry(outerTxId, "outer-tx", wfId);
        var outerSiblingEntry = new ActivityInstanceEntry(outerSiblingId, "outer-sibling", wfId, scopeId: outerTxId);
        var cancelEntry = new ActivityInstanceEntry(cancelOuterId, "cancel-outer", wfId, scopeId: outerTxId);
        // Inner TX was active inside outer TX scope and already ended — not in ActiveEntryIds
        var innerTxEntry = new ActivityInstanceEntry(innerTxId, "inner-tx", wfId, scopeId: outerTxId);

        state.AddEntries([outerTxEntry, outerSiblingEntry, cancelEntry, innerTxEntry]);
        state.CompleteEntries([cancelEntry, innerTxEntry]); // inner TX is done (Hazard), cancel-outer fired

        // Seed inner TX with Hazard outcome (inner TX ended in Hazard before outer cancel fires)
        execution.SetTransactionOutcomeHazard(innerTxId, "500", "inner task escaped");
        execution.ClearUncommittedEvents();

        // Act
        var effects = execution.InitiateTransactionCancelFlowIfNeeded();

        var events = execution.GetUncommittedEvents();

        // Outer TX outcome must be Hazard (cascaded from inner TX), not Cancelled
        var outcomeSet = events.OfType<TransactionOutcomeSet>()
            .FirstOrDefault(e => e.TransactionInstanceId == outerTxId);
        Assert.IsNotNull(outcomeSet, "Outer TX outcome must be set");
        Assert.AreEqual(TransactionOutcome.Hazard, outcomeSet.Outcome,
            "Outer TX outcome must be Hazard (cascaded from inner)");
        Assert.AreEqual(TransactionOutcome.Hazard, state.TransactionOutcomes[outerTxId].Outcome);

        // CancelScopeChildren must have run — the outer sibling task is cancelled
        var cancelledActivities = events.OfType<ActivityCancelled>()
            .Select(e => e.ActivityInstanceId).ToList();
        Assert.IsTrue(cancelledActivities.Contains(outerSiblingId),
            "Outer sibling task must be cancelled by CancelScopeChildren");

        // No compensation walk must be started for the outer TX (walk skipped)
        Assert.IsFalse(events.OfType<CompensationWalkStarted>().Any(),
            "No compensation walk must start for outer TX in Hazard cascade");

        // No walk abort (inner TX had no active walk to abort)
        Assert.IsFalse(events.OfType<CompensationWalkAborted>().Any(),
            "No CompensationWalkAborted expected (inner TX had no active walk)");

        // Effects list may be empty for plain tasks (no infrastructure cleanup needed);
        // the cancellation is expressed via ActivityCancelled domain events above.
    }

    // Guard: if no descendant has Hazard, normal cancel flow still runs
    [TestMethod]
    public void NestedTx_OuterCancels_NoDescendantHazard_NormalCancelFlow()
    {
        var (execution, state) = CreateExecution();
        var wfId = state.Id;
        var outerTxId = Guid.NewGuid();
        var cancelOuterId = Guid.NewGuid();

        var outerTxEntry = new ActivityInstanceEntry(outerTxId, "outer-tx", wfId);
        var cancelEntry = new ActivityInstanceEntry(cancelOuterId, "cancel-outer", wfId, scopeId: outerTxId);
        state.AddEntries([outerTxEntry, cancelEntry]);
        state.CompleteEntries([cancelEntry]);
        execution.ClearUncommittedEvents();

        execution.InitiateTransactionCancelFlowIfNeeded();

        var events = execution.GetUncommittedEvents();

        var outcomeSet = events.OfType<TransactionOutcomeSet>()
            .FirstOrDefault(e => e.TransactionInstanceId == outerTxId);
        Assert.IsNotNull(outcomeSet, "Outer TX outcome must be set");
        Assert.AreEqual(TransactionOutcome.Cancelled, outcomeSet.Outcome,
            "Without descendant Hazard, outcome is Cancelled");

        // Walk starts
        Assert.IsTrue(events.OfType<CompensationWalkStarted>().Any(),
            "Normal cancel flow must start a compensation walk");
    }

    // Guard: idempotency — calling twice doesn't re-process a Hazard outer TX
    [TestMethod]
    public void NestedTx_OuterCancels_AfterInnerHazard_Idempotent()
    {
        var (execution, state) = CreateExecution();
        var wfId = state.Id;
        var outerTxId = Guid.NewGuid();
        var innerTxId = Guid.NewGuid();
        var cancelOuterId = Guid.NewGuid();

        var outerTxEntry = new ActivityInstanceEntry(outerTxId, "outer-tx", wfId);
        var cancelEntry = new ActivityInstanceEntry(cancelOuterId, "cancel-outer", wfId, scopeId: outerTxId);
        var innerTxEntry = new ActivityInstanceEntry(innerTxId, "inner-tx", wfId, scopeId: outerTxId);
        state.AddEntries([outerTxEntry, cancelEntry, innerTxEntry]);
        state.CompleteEntries([cancelEntry, innerTxEntry]);

        execution.SetTransactionOutcomeHazard(innerTxId, "500", "inner error");
        execution.ClearUncommittedEvents();

        // First call
        execution.InitiateTransactionCancelFlowIfNeeded();
        execution.ClearUncommittedEvents();

        // Second call — idempotency check (Hazard already set on outer TX) must skip
        execution.InitiateTransactionCancelFlowIfNeeded();
        var events = execution.GetUncommittedEvents();

        Assert.IsFalse(events.OfType<TransactionOutcomeSet>()
            .Any(e => e.TransactionInstanceId == outerTxId),
            "Second call must not re-emit outcome for already-Hazard outer TX");
    }
}
