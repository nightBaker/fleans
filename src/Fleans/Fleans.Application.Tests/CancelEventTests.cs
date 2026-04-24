using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class CancelEventTests : WorkflowTestBase
{
    private static ScriptTask AutoTask(string id) => new(id, "PASS", "csharp");

    /// <summary>
    /// Builds a standard cancel-event workflow:
    ///   Root: start → tx → [cancelBoundary → recovery → end]
    ///   tx:   tx_start → tx_task_a → cancel_end
    ///   Optionally: CompensationBoundaryEvent(cb_a, tx_task_a, handler_a) inside tx
    /// </summary>
    private static WorkflowDefinition BuildCancelWorkflow(
        string workflowId,
        bool withCompensation = false,
        bool useManualHandlers = false)
    {
        const string txId = "tx1";

        var txStart = new StartEvent("tx_start");
        var txTaskA = new TaskActivity("tx_task_a");
        var cancelEnd = new CancelEndEvent("cancel_end");

        var txActivities = new List<Activity> { txStart, txTaskA, cancelEnd };
        var txFlows = new List<SequenceFlow>
        {
            new("tsf1", txStart, txTaskA),
            new("tsf2", txTaskA, cancelEnd)
        };

        if (withCompensation)
        {
            var handlerA = useManualHandlers
                ? (Activity)new TaskActivity("handler_a")
                : AutoTask("handler_a");
            var cbA = new CompensationBoundaryEvent("cb_a", "tx_task_a", "handler_a");
            txActivities.Add(handlerA);
            txActivities.Add(cbA);
        }

        var tx = new Transaction(txId)
        {
            Activities = txActivities,
            SequenceFlows = txFlows
        };

        var start = new StartEvent("start");
        var cancelBoundary = new CancelBoundaryEvent("cb_tx", txId);
        var recoveryTask = AutoTask("recovery_task");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, tx, cancelBoundary, recoveryTask, end],
            SequenceFlows =
            [
                new("f1", start, tx),
                new("f2", cancelBoundary, recoveryTask),
                new("f3", recoveryTask, end)
            ]
        };
    }

    [TestMethod]
    public async Task Cancel_OutcomeIsCancelled_WhenCancelEndEventReached()
    {
        var workflow = BuildCancelWorkflow("cancel-outcome-test");
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        var instanceId = grain.GetPrimaryKey();

        await grain.CompleteActivity("tx_task_a", new ExpandoObject());

        var snapshot = await WaitForCondition(
            instanceId,
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted, "Workflow should reach completed state");

        var outcomes = await grain.GetTransactionOutcomes();
        Assert.AreEqual(1, outcomes.Count, "Exactly one transaction outcome expected");
        Assert.AreEqual(TransactionOutcome.Cancelled, outcomes.Values.Single().Outcome,
            "Transaction outcome must be Cancelled");
    }

    [TestMethod]
    public async Task Cancel_CancelBoundaryTaken_RecoveryPathCompletes()
    {
        var workflow = BuildCancelWorkflow("cancel-boundary-path-test");
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        var instanceId = grain.GetPrimaryKey();

        await grain.CompleteActivity("tx_task_a", new ExpandoObject());

        var snapshot = await WaitForCondition(
            instanceId,
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted, "Workflow should complete via cancel boundary path");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "cb_tx"),
            "CancelBoundaryEvent should be activated and completed");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "recovery_task"),
            "Recovery task should run after cancel boundary fires");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "End event should complete after recovery task");
    }

    [TestMethod]
    public async Task Cancel_WithOneCompensationHandler_HandlerRunsBeforeBoundaryFires()
    {
        var workflow = BuildCancelWorkflow("cancel-one-handler-test", withCompensation: true);
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        var instanceId = grain.GetPrimaryKey();

        await grain.CompleteActivity("tx_task_a", new ExpandoObject());

        var snapshot = await WaitForCondition(
            instanceId,
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_a"),
            "Compensation handler_a should have executed during cancel flow");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "cb_tx"),
            "Cancel boundary should fire after compensation walk completes");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "recovery_task"),
            "Recovery task should complete");

        var outcomes = await grain.GetTransactionOutcomes();
        Assert.AreEqual(TransactionOutcome.Cancelled, outcomes.Values.Single().Outcome);
    }

    [TestMethod]
    public async Task Cancel_WithMultipleCompletedTasks_HandlersRunInReverseOrder()
    {
        // Transaction: tx_start → tx_task_a → tx_task_b → cancel_end
        // Compensation: cb_a on tx_task_a → handler_a, cb_b on tx_task_b → handler_b
        // Reverse order: handler_b (task_b completed last) runs before handler_a
        const string txId = "tx1";

        var txStart = new StartEvent("tx_start");
        var txTaskA = new TaskActivity("tx_task_a");
        var txTaskB = new TaskActivity("tx_task_b");
        var cancelEnd = new CancelEndEvent("cancel_end");
        var handlerA = AutoTask("handler_a");
        var handlerB = AutoTask("handler_b");
        var cbA = new CompensationBoundaryEvent("cb_a", "tx_task_a", "handler_a");
        var cbB = new CompensationBoundaryEvent("cb_b", "tx_task_b", "handler_b");

        var tx = new Transaction(txId)
        {
            Activities = [txStart, txTaskA, txTaskB, cancelEnd, handlerA, handlerB, cbA, cbB],
            SequenceFlows =
            [
                new("tsf1", txStart, txTaskA),
                new("tsf2", txTaskA, txTaskB),
                new("tsf3", txTaskB, cancelEnd)
            ]
        };

        var start = new StartEvent("start");
        var cancelBoundary = new CancelBoundaryEvent("cb_tx", txId);
        var recoveryTask = AutoTask("recovery_task");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cancel-reverse-order",
            Activities = [start, tx, cancelBoundary, recoveryTask, end],
            SequenceFlows =
            [
                new("f1", start, tx),
                new("f2", cancelBoundary, recoveryTask),
                new("f3", recoveryTask, end)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        var instanceId = grain.GetPrimaryKey();

        await grain.CompleteActivity("tx_task_a", new ExpandoObject());
        await grain.CompleteActivity("tx_task_b", new ExpandoObject());

        var snapshot = await WaitForCondition(
            instanceId,
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_a"),
            "handler_a should have run");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "handler_b"),
            "handler_b should have run");

        // handler_b (for task_b, which completed last) should run before handler_a
        var handlerACompletedAt = snapshot.CompletedActivities
            .First(a => a.ActivityId == "handler_a").CompletedAt;
        var handlerBCompletedAt = snapshot.CompletedActivities
            .First(a => a.ActivityId == "handler_b").CompletedAt;
        Assert.IsTrue(handlerBCompletedAt <= handlerACompletedAt,
            "handler_b should run before handler_a (reverse completion order)");

        var outcomes = await grain.GetTransactionOutcomes();
        Assert.AreEqual(TransactionOutcome.Cancelled, outcomes.Values.Single().Outcome);
    }

    [TestMethod]
    public async Task Cancel_OutcomeNotOverriddenToCompleted_ByScopeAutoCompletion()
    {
        // The Transaction scope auto-completion logic must not set outcome to Completed
        // after the cancel flow has already set it to Cancelled.
        var workflow = BuildCancelWorkflow("cancel-no-complete-override", withCompensation: true);
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        var instanceId = grain.GetPrimaryKey();

        await grain.CompleteActivity("tx_task_a", new ExpandoObject());

        var snapshot = await WaitForCondition(
            instanceId,
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted);

        var outcomes = await grain.GetTransactionOutcomes();
        var outcome = outcomes.Values.Single();
        Assert.AreEqual(TransactionOutcome.Cancelled, outcome.Outcome,
            "Cancelled outcome must not be overridden to Completed by scope auto-completion");
    }

    [TestMethod]
    public async Task Cancel_NoCompensationHandlers_BoundaryStillFires()
    {
        // When there are no compensation boundaries, cancel flow skips compensation walk
        // but still fires the CancelBoundaryEvent.
        var workflow = BuildCancelWorkflow("cancel-no-handlers-test", withCompensation: false);
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        var instanceId = grain.GetPrimaryKey();

        await grain.CompleteActivity("tx_task_a", new ExpandoObject());

        var snapshot = await WaitForCondition(
            instanceId,
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted,
            "Workflow should complete even with no compensation handlers");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "cb_tx"),
            "Cancel boundary should still fire when there are no compensation handlers");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "recovery_task"),
            "Recovery path should execute");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "End event should complete");
    }

    [TestMethod]
    public async Task Cancel_CompletedTaskIsNotCancelled_OnlyCancelledByBoundary()
    {
        // tx_task_a was manually completed (not cancelled). After cancel_end fires,
        // the TX scope's remaining ACTIVE activities get cancelled.
        // Since the flow is sequential and tx_task_a is already done, it should
        // remain in completed activities without IsCancelled=true.
        var workflow = BuildCancelWorkflow("cancel-no-spurious-cancel", withCompensation: false);
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        await grain.StartWorkflow();
        var instanceId = grain.GetPrimaryKey();

        await grain.CompleteActivity("tx_task_a", new ExpandoObject());

        var snapshot = await WaitForCondition(
            instanceId,
            s => s.IsCompleted,
            timeoutMs: 10000);

        Assert.IsTrue(snapshot.IsCompleted);

        var taskAEntry = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "tx_task_a");
        Assert.IsNotNull(taskAEntry, "tx_task_a should appear in completed activities");
        Assert.IsFalse(taskAEntry.IsCancelled,
            "tx_task_a was manually completed before cancel_end fired — it should not be marked cancelled");
        Assert.IsNull(taskAEntry.CancellationReason,
            "tx_task_a should have no cancellation reason");
    }
}
