using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class TransactionSubProcessTests : WorkflowTestBase
{
    private static Transaction CreateSimpleTransaction(string id)
    {
        var txStart = new StartEvent($"{id}_start");
        var txTask1 = new TaskActivity($"{id}_task1");
        var txTask2 = new TaskActivity($"{id}_task2");
        var txEnd = new EndEvent($"{id}_end");

        return new Transaction(id)
        {
            Activities = [txStart, txTask1, txTask2, txEnd],
            SequenceFlows =
            [
                new SequenceFlow($"{id}_sf1", txStart, txTask1),
                new SequenceFlow($"{id}_sf2", txTask1, txTask2),
                new SequenceFlow($"{id}_sf3", txTask2, txEnd)
            ]
        };
    }

    [TestMethod]
    public async Task Transaction_HappyPath_CompletesWorkflow_AndSetsCompletedOutcome()
    {
        // Arrange: start → transaction(start→task1→task2→end) → end
        var start = new StartEvent("start");
        var tx = CreateSimpleTransaction("tx1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "tx-happy-path",
            Activities = [start, tx, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, tx),
                new SequenceFlow("f2", tx, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();

        // Act: complete task1 inside transaction
        var snap1 = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snap1);
        Assert.IsTrue(snap1.ActiveActivities.Any(a => a.ActivityId == "tx1_task1"),
            "tx1_task1 should be active after start");

        await workflowInstance.CompleteActivity("tx1_task1", new ExpandoObject());

        // Act: complete task2 inside transaction
        var snap2 = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snap2);
        Assert.IsTrue(snap2.ActiveActivities.Any(a => a.ActivityId == "tx1_task2"),
            "tx1_task2 should be active after task1 completes");

        await workflowInstance.CompleteActivity("tx1_task2", new ExpandoObject());

        // Assert: workflow completes
        var finalSnap = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(finalSnap);
        Assert.IsTrue(finalSnap.IsCompleted, "Workflow should complete after transaction happy path");
        Assert.IsTrue(finalSnap.CompletedActivities.Any(a => a.ActivityId == "end"),
            "End event should be in completed activities");
        Assert.IsTrue(finalSnap.CompletedActivities.Any(a => a.ActivityId == "tx1_task1"));
        Assert.IsTrue(finalSnap.CompletedActivities.Any(a => a.ActivityId == "tx1_task2"));

        // Assert: outcome is Completed
        var outcomes = await workflowInstance.GetTransactionOutcomes();
        Assert.AreEqual(1, outcomes.Count, "Exactly one transaction outcome should be recorded");
        var outcome = outcomes.Values.Single();
        Assert.AreEqual(TransactionOutcome.Completed, outcome.Outcome);
        Assert.IsNull(outcome.ErrorCode);
        Assert.IsNull(outcome.ErrorMessage);
    }

    [TestMethod]
    public async Task Transaction_HappyPath_VariablesMergedToParentScope()
    {
        // Arrange: start → parent_task → transaction(start→inner_task→end) → check_task → end
        var start = new StartEvent("start");
        var parentTask = new TaskActivity("parent_task");

        var txStart = new StartEvent("tx_start");
        var txTask = new TaskActivity("tx_task");
        var txEnd = new EndEvent("tx_end");
        var tx = new Transaction("tx1")
        {
            Activities = [txStart, txTask, txEnd],
            SequenceFlows =
            [
                new SequenceFlow("tsf1", txStart, txTask),
                new SequenceFlow("tsf2", txTask, txEnd)
            ]
        };

        var checkTask = new TaskActivity("check_task");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "tx-var-merge",
            Activities = [start, parentTask, tx, checkTask, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, parentTask),
                new SequenceFlow("f2", parentTask, tx),
                new SequenceFlow("f3", tx, checkTask),
                new SequenceFlow("f4", checkTask, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();

        // Set a variable via parentTask
        dynamic parentVars = new ExpandoObject();
        parentVars.parentValue = "from-parent";
        await workflowInstance.CompleteActivity("parent_task", parentVars);

        // Complete tx_task with transaction-scoped variable
        dynamic txVars = new ExpandoObject();
        txVars.txValue = "from-transaction";
        await workflowInstance.CompleteActivity("tx_task", txVars);

        // check_task should be active in parent scope with merged variables
        var snap = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snap);
        Assert.IsFalse(snap.IsCompleted, "Should be paused at check_task");
        Assert.IsTrue(snap.ActiveActivities.Any(a => a.ActivityId == "check_task"));

        var checkTaskEntry = snap.ActiveActivities.First(a => a.ActivityId == "check_task");
        var parentVal = await workflowInstance.GetVariable(checkTaskEntry.VariablesStateId, "parentValue");
        var txVal = await workflowInstance.GetVariable(checkTaskEntry.VariablesStateId, "txValue");

        Assert.AreEqual("from-parent", parentVal, "Parent variable should still be accessible");
        Assert.AreEqual("from-transaction", txVal, "Transaction variable should be merged into parent scope");

        await workflowInstance.CompleteActivity("check_task", new ExpandoObject());

        var finalSnap = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnap!.IsCompleted);
    }

    [TestMethod]
    public async Task Transaction_OutcomeKeyedByActivityInstanceId_IsUnique()
    {
        // Verifies that TransactionOutcome is keyed by the INSTANCE id (Guid), not just activity id,
        // so a transaction activity can be re-entered (e.g. in a loop) with independent outcome tracking.
        var start = new StartEvent("start");
        var txStart = new StartEvent("tx_start");
        var txTask = new TaskActivity("tx_task");
        var txEnd = new EndEvent("tx_end");
        var tx = new Transaction("tx1")
        {
            Activities = [txStart, txTask, txEnd],
            SequenceFlows =
            [
                new SequenceFlow("tsf1", txStart, txTask),
                new SequenceFlow("tsf2", txTask, txEnd)
            ]
        };
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "tx-outcome-keying",
            Activities = [start, tx, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, tx),
                new SequenceFlow("f2", tx, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();

        // Get the transaction activity instance id from snapshot
        var snap = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snap);
        var txHost = snap.ActiveActivities.FirstOrDefault(a => a.ActivityId == "tx1");
        // tx1 host may not show in the snapshot if the engine has already entered the subprocess scope
        // (the task inside is what shows as active). The outcome is keyed by the host instance id.

        await workflowInstance.CompleteActivity("tx_task", new ExpandoObject());

        var outcomes = await workflowInstance.GetTransactionOutcomes();
        Assert.AreEqual(1, outcomes.Count, "Exactly one outcome entry should exist");

        // The key must be a non-empty Guid (activity instance id)
        var key = outcomes.Keys.Single();
        Assert.AreNotEqual(Guid.Empty, key, "Outcome key must be the activity instance id, not empty");

        Assert.AreEqual(TransactionOutcome.Completed, outcomes[key].Outcome);
    }

    [TestMethod]
    public async Task Transaction_IsSubProcess_ScopeCleanedUpOnCompletion()
    {
        var start = new StartEvent("start");
        var txStart = new StartEvent("tx_start");
        var txTask = new TaskActivity("tx_task");
        var txEnd = new EndEvent("tx_end");
        var tx = new Transaction("tx1")
        {
            Activities = [txStart, txTask, txEnd],
            SequenceFlows =
            [
                new SequenceFlow("tsf1", txStart, txTask),
                new SequenceFlow("tsf2", txTask, txEnd)
            ]
        };
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "tx-scope-cleanup",
            Activities = [start, tx, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, tx),
                new SequenceFlow("f2", tx, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();

        // Mid-execution: at least 2 scopes (root + transaction child)
        var midSnap = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnap!.VariableStates.Count >= 2,
            $"Should have at least 2 scopes while inside transaction, got {midSnap.VariableStates.Count}");

        await workflowInstance.CompleteActivity("tx_task", new ExpandoObject());

        // After completion: only root scope remains
        var finalSnap = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnap!.IsCompleted);
        Assert.AreEqual(1, finalSnap.VariableStates.Count,
            "Only root scope should remain after Transaction scope cleanup");
    }

    [TestMethod]
    public async Task NestedTransaction_HappyPath_BothComplete_AndVariablesMergeOutwards()
    {
        // Phase A acceptance test for #307: a <transaction> nested inside another <transaction>
        // runs the happy path end-to-end and records `Completed` for both transactions. The
        // inner transaction's variable surfaces at the workflow root, which transitively proves
        // the existing scope/variable-merge plumbing carries values inner → outer → root for
        // nested transactions — the precondition the design plan calls out before the
        // compensation-walk refactor lands.
        //
        // start → outer_tx(outer_start → inner_tx(inner_start → inner_task → inner_end) → outer_end) → end
        var start = new StartEvent("start");

        var innerStart = new StartEvent("inner_start");
        var innerTask = new TaskActivity("inner_task");
        var innerEnd = new EndEvent("inner_end");
        var innerTx = new Transaction("inner_tx")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("isf1", innerStart, innerTask),
                new SequenceFlow("isf2", innerTask, innerEnd)
            ]
        };

        var outerStart = new StartEvent("outer_start");
        var outerEnd = new EndEvent("outer_end");
        var outerTx = new Transaction("outer_tx")
        {
            Activities = [outerStart, innerTx, outerEnd],
            SequenceFlows =
            [
                new SequenceFlow("osf1", outerStart, innerTx),
                new SequenceFlow("osf2", innerTx, outerEnd)
            ]
        };

        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "nested-tx-happy-path",
            Activities = [start, outerTx, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, outerTx),
                new SequenceFlow("f2", outerTx, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();

        // inner_task should be the first task to become active (inside the inner transaction's scope).
        var snapInner = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapInner);
        Assert.IsTrue(snapInner.ActiveActivities.Any(a => a.ActivityId == "inner_task"),
            "inner_task should be active inside the inner transaction");

        // Mid-flight: at least 3 scopes should coexist — root, outer_tx, inner_tx — proving
        // the engine creates an independent scope for each transaction depth.
        Assert.IsTrue(snapInner.VariableStates.Count >= 3,
            $"Expected at least 3 nested scopes (root + outer_tx + inner_tx), got {snapInner.VariableStates.Count}");

        // Set a variable on inner_task — it must surface at the workflow root after both
        // transactions commit (inner → outer → root merge).
        dynamic innerVars = new ExpandoObject();
        innerVars.innerValue = "from-inner";
        await workflowInstance.CompleteActivity("inner_task", innerVars);

        var finalSnap = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(finalSnap);
        Assert.IsTrue(finalSnap.IsCompleted,
            "Workflow should complete after the inner task — both transactions auto-commit on the happy path");
        Assert.AreEqual(1, finalSnap.VariableStates.Count,
            "Only the root scope should remain after both transaction scopes clean up");

        var rootScope = finalSnap.VariableStates.Single();
        Assert.IsTrue(rootScope.Variables.TryGetValue("innerValue", out var innerAtRoot),
            "Inner transaction variable should reach the workflow root scope (inner → outer → root merge)");
        Assert.AreEqual("from-inner", innerAtRoot);

        // Both transactions must have recorded Completed outcomes — proving each nested instance
        // is independently tracked by the existing TransactionOutcomes plumbing.
        var outcomes = await workflowInstance.GetTransactionOutcomes();
        Assert.AreEqual(2, outcomes.Count,
            "Two transaction outcomes should be recorded — one for inner, one for outer");
        Assert.IsTrue(outcomes.Values.All(o => o.Outcome == TransactionOutcome.Completed),
            "Both nested transactions must record Completed outcomes on the happy path");
        Assert.IsTrue(outcomes.Values.All(o => o.ErrorCode is null && o.ErrorMessage is null),
            "Completed outcomes must carry no error code or message");
    }
}
