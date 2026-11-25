using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Transaction outcomes that the domain tests cover only with seeded state (<c>TransactionHazardDomainTests</c>,
/// <c>NestedTxPhaseB/CDomainTests</c>), run end to end on a silo: a failure inside a transaction (Hazard) with and
/// without an error boundary on the transaction, and the nested scenarios C and F of
/// <c>tests/manual/53-nested-transaction</c>.
/// </summary>
[TestClass]
public class TransactionHazardTests : WorkflowTestBase
{
    private const string CorrelationKey = "tx-hazard";

    [TestMethod]
    public async Task Hazard_WithAnErrorBoundaryOnTheTransaction_RecordsHazard_AndTakesTheRecoveryPath()
    {
        // Arrange — start → tx(t1 → t2) → end; error boundary on tx → recovery → recovery_end
        var workflow = SingleTransaction("tx-hazard-boundary", withErrorBoundary: true);
        var instance = await Start(workflow);
        await instance.CompleteActivity("t1", new ExpandoObject());
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("t2"));

        // Act
        await instance.FailActivity("t2", new InvalidOperationException("payment processor unavailable"));

        // Assert
        var recovering = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("recovery"));
        Assert.AreEqual(TransactionOutcome.Hazard, (await OutcomeOf(instance, recovering, "tx")).Outcome);
        CollectionAssert.DoesNotContain(recovering.CompletedActivityIds, "end", "the normal path is not taken");
        await instance.CompleteActivity("recovery", new ExpandoObject());
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        CollectionAssert.Contains(done.CompletedActivityIds, "recovery_end");
        Assert.IsFalse(done.IsFailed);
    }

    [TestMethod]
    public async Task Hazard_WithoutAnErrorBoundary_FailsTheInstance_AndRecordsHazard()
    {
        // Arrange
        var workflow = SingleTransaction("tx-hazard-unhandled", withErrorBoundary: false);
        var instance = await Start(workflow);
        await instance.CompleteActivity("t1", new ExpandoObject());
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("t2"));

        // Act
        await instance.FailActivity("t2", new InvalidOperationException("boom"));

        // Assert
        var state = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.IsFailed || s.IsCompleted || s.CompletedActivities.Any(a => a.ActivityId == "tx" && a.ErrorState is not null));
        CollectionAssert.DoesNotContain(state.CompletedActivityIds, "end", "the normal path is not taken");
        Assert.AreEqual(TransactionOutcome.Hazard, (await OutcomeOf(instance, state, "tx")).Outcome);
        Assert.IsNotNull(state.CompletedActivities.Single(a => a.ActivityId == "tx").ErrorState,
            "the transaction itself fails");
    }

    [TestMethod]
    public async Task NestedScenarioC_OuterCancelsAfterTheInnerCommitted_TakesTheOuterCancelBoundary()
    {
        // Arrange
        var instance = await Start(Nested("nested-tx-c"));
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("inner_work"));
        await instance.CompleteActivity("inner_work", new ExpandoObject());
        var waiting = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("cancel_catch"));
        CollectionAssert.Contains(waiting.CompletedActivityIds, "inner_tx");

        // Act
        await DeliverWhenSubscribed("trigger-outer-cancel");

        // Assert
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        CollectionAssert.IsSubsetOf(new[] { "inner_work", "inner_tx", "cancel_catch", "outer_cancel_end", "cancel_recovery_end" },
            done.CompletedActivityIds.ToList());
        CollectionAssert.DoesNotContain(done.CompletedActivityIds, "hazard_recovery_end", "the outer error boundary does not fire");
        Assert.AreEqual(TransactionOutcome.Completed, (await OutcomeOf(instance, done, "inner_tx")).Outcome);
        Assert.AreEqual(TransactionOutcome.Cancelled, (await OutcomeOf(instance, done, "outer_tx")).Outcome);
    }

    [TestMethod]
    public async Task NestedScenarioF_InnerHazardCascadesToTheOuterErrorBoundary()
    {
        // Arrange
        var instance = await Start(Nested("nested-tx-f"));
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("inner_work"));

        // Act — the error escapes the inner transaction, which has no boundary of its own
        await instance.FailActivity("inner_work", new InvalidOperationException("inner failure"));

        // Assert
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsFalse(done.IsFailed);
        CollectionAssert.Contains(done.CompletedActivityIds, "hazard_recovery_end");
        CollectionAssert.DoesNotContain(done.CompletedActivityIds, "cancel_recovery_end");
        CollectionAssert.DoesNotContain(done.CompletedActivityIds, "process_end");
        Assert.AreEqual(TransactionOutcome.Hazard, (await OutcomeOf(instance, done, "inner_tx")).Outcome);
        Assert.AreEqual(TransactionOutcome.Hazard, (await OutcomeOf(instance, done, "outer_tx")).Outcome);
    }

    // --- builders ---

    private static WorkflowDefinition SingleTransaction(string id, bool withErrorBoundary)
    {
        var txStart = new StartEvent("tx_start");
        var t1 = new TaskActivity("t1");
        var t2 = new TaskActivity("t2");
        var txEnd = new EndEvent("tx_end");
        var tx = new Transaction("tx")
        {
            Activities = [txStart, t1, t2, txEnd],
            SequenceFlows =
            [
                new SequenceFlow("tf1", txStart, t1),
                new SequenceFlow("tf2", t1, t2),
                new SequenceFlow("tf3", t2, txEnd)
            ]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        List<Activity> activities = [start, tx, end];
        List<SequenceFlow> flows = [new("f1", start, tx), new("f2", tx, end)];
        if (withErrorBoundary)
        {
            var boundary = new BoundaryErrorEvent("tx_error", "tx", null);
            var recovery = new TaskActivity("recovery");
            var recoveryEnd = new EndEvent("recovery_end");
            activities.AddRange([boundary, recovery, recoveryEnd]);
            flows.AddRange([new("f3", boundary, recovery), new("f4", recovery, recoveryEnd)]);
        }
        return new WorkflowDefinition { WorkflowId = id, Activities = activities, SequenceFlows = flows };
    }

    /// <summary>
    /// start → outer_tx(inner_tx(inner_work) → cancel_catch → outer_cancel_end) → process_end, with a cancel boundary
    /// (→ cancel_recovery_end) and a catch-all error boundary (→ hazard_recovery_end) on outer_tx.
    /// </summary>
    private static WorkflowDefinition Nested(string id)
    {
        var innerStart = new StartEvent("inner_start");
        var innerWork = new TaskActivity("inner_work");
        var innerCompensate = new ScriptTask("inner_compensate", "PASS", "csharp");
        var innerEnd = new EndEvent("inner_end");
        var innerTx = new Transaction("inner_tx")
        {
            Activities =
            [
                innerStart, innerWork, innerCompensate, innerEnd,
                new CompensationBoundaryEvent("inner_compensate_boundary", "inner_work", "inner_compensate")
            ],
            SequenceFlows = [new("if1", innerStart, innerWork), new("if2", innerWork, innerEnd)]
        };

        var outerStart = new StartEvent("outer_start");
        var cancelCatch = new MessageIntermediateCatchEvent("cancel_catch", "msgCancel");
        var outerCancelEnd = new CancelEndEvent("outer_cancel_end");
        var outerTx = new Transaction("outer_tx")
        {
            Activities = [outerStart, innerTx, cancelCatch, outerCancelEnd],
            SequenceFlows =
            [
                new("of1", outerStart, innerTx),
                new("of2", innerTx, cancelCatch),
                new("of3", cancelCatch, outerCancelEnd)
            ]
        };

        var start = new StartEvent("start");
        var processEnd = new EndEvent("process_end");
        var cancelBoundary = new CancelBoundaryEvent("outer_cancel", "outer_tx");
        var errorBoundary = new BoundaryErrorEvent("outer_error", "outer_tx", null);
        var cancelRecoveryEnd = new EndEvent("cancel_recovery_end");
        var hazardRecoveryEnd = new EndEvent("hazard_recovery_end");
        return new WorkflowDefinition
        {
            WorkflowId = id,
            Activities = [start, outerTx, processEnd, cancelBoundary, errorBoundary, cancelRecoveryEnd, hazardRecoveryEnd],
            SequenceFlows =
            [
                new("f1", start, outerTx),
                new("f2", outerTx, processEnd),
                new("f3", cancelBoundary, cancelRecoveryEnd),
                new("f4", errorBoundary, hazardRecoveryEnd)
            ],
            Messages = [new MessageDefinition("msgCancel", "trigger-outer-cancel", "= txKey")]
        };
    }

    // --- helpers ---


    private async Task<IWorkflowInstanceGrain> Start(WorkflowDefinition workflow)
    {
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        dynamic vars = new ExpandoObject();
        vars.txKey = CorrelationKey + "-" + workflow.WorkflowId;
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)vars);
        return instance;
    }

    private async Task DeliverWhenSubscribed(string messageName)
    {
        var delivered = await DeliverMessageWhenSubscribed(messageName, CorrelationKey + "-nested-tx-c", new ExpandoObject());
        Assert.IsTrue(delivered, $"{messageName} reaches the outer catch");
    }

    private static async Task<TransactionOutcomeRecord> OutcomeOf(IWorkflowInstanceGrain instance, InstanceStateSnapshot state, string transactionId)
    {
        var outcomes = await instance.GetTransactionOutcomes();
        var hostId = state.CompletedActivities.Concat(state.ActiveActivities)
            .Single(a => a.ActivityId == transactionId).ActivityInstanceId;
        Assert.IsTrue(outcomes.TryGetValue(hostId, out var outcome), $"{transactionId} has an outcome");
        return outcome;
    }
}
