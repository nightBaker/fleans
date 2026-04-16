using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>
/// Unit tests for Transaction Sub-Process domain methods on WorkflowExecution:
/// SetTransactionOutcomeCompleted, SetTransactionOutcomeCancelled, SetTransactionOutcomeHazard.
/// </summary>
[TestClass]
public class TransactionOutcomeDomainTests
{
    private static (WorkflowExecution execution, WorkflowInstanceState state) CreateExecution()
    {
        var definition = new WorkflowDefinition
        {
            WorkflowId = "tx-test",
            Activities = [],
            SequenceFlows = [],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        return (execution, state);
    }

    // ── SetTransactionOutcomeCompleted ─────────────────────────────────────────

    [TestMethod]
    public void SetTransactionOutcomeCompleted_UpdatesState_WithCompletedOutcome()
    {
        var (execution, state) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeCompleted(instanceId);

        Assert.IsTrue(state.TransactionOutcomes.ContainsKey(instanceId));
        var record = state.TransactionOutcomes[instanceId];
        Assert.AreEqual(TransactionOutcome.Completed, record.Outcome);
        Assert.IsNull(record.ErrorCode);
        Assert.IsNull(record.ErrorMessage);
    }

    [TestMethod]
    public void SetTransactionOutcomeCompleted_EmitsTransactionOutcomeSetEvent()
    {
        var (execution, _) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeCompleted(instanceId);

        var events = execution.GetUncommittedEvents();
        var evt = events.OfType<TransactionOutcomeSet>().Single();
        Assert.AreEqual(instanceId, evt.TransactionInstanceId);
        Assert.AreEqual(TransactionOutcome.Completed, evt.Outcome);
    }

    [TestMethod]
    public void SetTransactionOutcomeCompleted_CalledTwice_IsIdempotent()
    {
        var (execution, state) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeCompleted(instanceId);
        execution.ClearUncommittedEvents();

        // Second call — must not throw, must not emit a new event
        execution.SetTransactionOutcomeCompleted(instanceId);
        var uncommitted = execution.GetUncommittedEvents();
        Assert.AreEqual(0, uncommitted.Count, "Second Completed call must not emit an event");
    }

    [TestMethod]
    public void SetTransactionOutcomeCompleted_WhenAlreadyCancelled_Throws()
    {
        var (execution, state) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeCancelled(instanceId);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => execution.SetTransactionOutcomeCompleted(instanceId));
    }

    [TestMethod]
    public void SetTransactionOutcomeCompleted_WhenAlreadyHazard_Throws()
    {
        var (execution, state) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeHazard(instanceId, 500, "error");

        Assert.ThrowsExactly<InvalidOperationException>(
            () => execution.SetTransactionOutcomeCompleted(instanceId));
    }

    // ── SetTransactionOutcomeCancelled ─────────────────────────────────────────

    [TestMethod]
    public void SetTransactionOutcomeCancelled_UpdatesState_WithCancelledOutcome()
    {
        var (execution, state) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeCancelled(instanceId);

        Assert.IsTrue(state.TransactionOutcomes.ContainsKey(instanceId));
        Assert.AreEqual(TransactionOutcome.Cancelled, state.TransactionOutcomes[instanceId].Outcome);
    }

    [TestMethod]
    public void SetTransactionOutcomeCancelled_EmitsTransactionOutcomeSetEvent()
    {
        var (execution, _) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeCancelled(instanceId);

        var events = execution.GetUncommittedEvents();
        var evt = events.OfType<TransactionOutcomeSet>().Single();
        Assert.AreEqual(instanceId, evt.TransactionInstanceId);
        Assert.AreEqual(TransactionOutcome.Cancelled, evt.Outcome);
    }

    [TestMethod]
    public void SetTransactionOutcomeCancelled_CalledTwice_IsIdempotent()
    {
        var (execution, _) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeCancelled(instanceId);
        execution.ClearUncommittedEvents();

        execution.SetTransactionOutcomeCancelled(instanceId);
        var uncommitted = execution.GetUncommittedEvents();
        Assert.AreEqual(0, uncommitted.Count, "Second Cancelled call must not emit an event");
    }

    [TestMethod]
    public void SetTransactionOutcomeCancelled_WhenAlreadyHazard_IsNoOp()
    {
        var (execution, state) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeHazard(instanceId, 500, "err");
        execution.ClearUncommittedEvents();

        // Cancelled after Hazard → Hazard wins, no-op, no event
        execution.SetTransactionOutcomeCancelled(instanceId);
        var uncommitted = execution.GetUncommittedEvents();
        Assert.AreEqual(0, uncommitted.Count, "Cancelled after Hazard must be a no-op");
        Assert.AreEqual(TransactionOutcome.Hazard, state.TransactionOutcomes[instanceId].Outcome);
    }

    [TestMethod]
    public void SetTransactionOutcomeCancelled_WhenAlreadyCompleted_Throws()
    {
        var (execution, _) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeCompleted(instanceId);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => execution.SetTransactionOutcomeCancelled(instanceId));
    }

    // ── SetTransactionOutcomeHazard ────────────────────────────────────────────

    [TestMethod]
    public void SetTransactionOutcomeHazard_UpdatesState_WithHazardOutcome()
    {
        var (execution, state) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeHazard(instanceId, 500, "internal error");

        Assert.IsTrue(state.TransactionOutcomes.ContainsKey(instanceId));
        var record = state.TransactionOutcomes[instanceId];
        Assert.AreEqual(TransactionOutcome.Hazard, record.Outcome);
        Assert.AreEqual(500, record.ErrorCode);
        Assert.AreEqual("internal error", record.ErrorMessage);
    }

    [TestMethod]
    public void SetTransactionOutcomeHazard_EmitsTransactionOutcomeSetEvent()
    {
        var (execution, _) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeHazard(instanceId, 400, "bad request");

        var events = execution.GetUncommittedEvents();
        var evt = events.OfType<TransactionOutcomeSet>().Single();
        Assert.AreEqual(instanceId, evt.TransactionInstanceId);
        Assert.AreEqual(TransactionOutcome.Hazard, evt.Outcome);
        Assert.AreEqual(400, evt.ErrorCode);
        Assert.AreEqual("bad request", evt.ErrorMessage);
    }

    [TestMethod]
    public void SetTransactionOutcomeHazard_CanOverwriteExistingHazard()
    {
        var (execution, state) = CreateExecution();
        var instanceId = Guid.NewGuid();

        execution.SetTransactionOutcomeHazard(instanceId, 500, "first");
        execution.SetTransactionOutcomeHazard(instanceId, 400, "second");

        var record = state.TransactionOutcomes[instanceId];
        Assert.AreEqual(TransactionOutcome.Hazard, record.Outcome);
        Assert.AreEqual(400, record.ErrorCode);
        Assert.AreEqual("second", record.ErrorMessage);
    }

    // ── Multiple independent transactions ──────────────────────────────────────

    [TestMethod]
    public void DifferentTransactionInstances_TrackOutcomesIndependently()
    {
        var (execution, state) = CreateExecution();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        execution.SetTransactionOutcomeCompleted(id1);
        execution.SetTransactionOutcomeCancelled(id2);

        Assert.AreEqual(TransactionOutcome.Completed, state.TransactionOutcomes[id1].Outcome);
        Assert.AreEqual(TransactionOutcome.Cancelled, state.TransactionOutcomes[id2].Outcome);
    }

    [TestMethod]
    public void EmptyWorkflow_TransactionOutcomes_InitiallyEmpty()
    {
        var (_, state) = CreateExecution();
        Assert.AreEqual(0, state.TransactionOutcomes.Count);
    }
}
