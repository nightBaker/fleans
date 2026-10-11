using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Mirrors the <c>tests/manual/53-nested-transaction</c> fixtures (#761): a compensation-bounded
/// script task inside an inner transaction, nested inside an outer transaction, followed by a
/// correlated message catch at the outer level.
/// </summary>
[TestClass]
public class NestedTransactionScenarioTests : WorkflowTestBase
{
    private const string CorrelationKey = "tx-761";

    private static WorkflowDefinition BuildWorkflow(string workflowId, bool innerCancels, bool withCorrelation = true)
    {
        var innerStart = new StartEvent("inner-start");
        var innerWork = new ScriptTask("inner-work", "PASS", "csharp");
        var innerCompensate = new ScriptTask("inner-compensate", "PASS", "csharp");
        Activity innerEnd = innerCancels ? new CancelEndEvent("inner-cancel-end") : new EndEvent("inner-end");
        var compBoundary = new CompensationBoundaryEvent("inner-compensate-boundary", "inner-work", "inner-compensate");

        var innerTx = new Transaction("inner-tx")
        {
            Activities = [innerStart, innerWork, innerCompensate, innerEnd, compBoundary],
            SequenceFlows =
            [
                new SequenceFlow("isf1", innerStart, innerWork),
                new SequenceFlow("isf2", innerWork, innerEnd)
            ]
        };

        var outerStart = new StartEvent("outer-start");
        var catchEvent = new MessageIntermediateCatchEvent("trigger-outer-complete-catch", "msgTriggerOuterComplete");
        var outerEnd = new EndEvent("outer-end");
        var outerActivities = new List<Activity> { outerStart, innerTx, catchEvent, outerEnd };
        var outerFlows = new List<SequenceFlow>
        {
            new("osf1", outerStart, innerTx),
            new("osf5", catchEvent, outerEnd)
        };
        if (innerCancels)
        {
            var cancelBoundary = new CancelBoundaryEvent("inner-tx-cancel-boundary", "inner-tx");
            var join = new ExclusiveGateway("join-after-inner");
            outerActivities.Add(cancelBoundary);
            outerActivities.Add(join);
            outerFlows.Add(new("osf2", innerTx, join));
            outerFlows.Add(new("osf3", cancelBoundary, join));
            outerFlows.Add(new("osf4", join, catchEvent));
        }
        else
        {
            outerFlows.Add(new("osf2", innerTx, catchEvent));
        }

        var outerTx = new Transaction("outer-tx")
        {
            Activities = outerActivities,
            SequenceFlows = outerFlows
        };

        var start = new StartEvent("start");
        var processEnd = new EndEvent("process-end");

        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, outerTx, processEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", start, outerTx),
                new SequenceFlow("sf2", outerTx, processEnd)
            ],
            Messages =
            [
                new MessageDefinition("msgTriggerOuterComplete", "trigger-outer-complete",
                    withCorrelation ? "= txKey" : null)
            ]
        };
    }

    private async Task<(IWorkflowInstanceGrain Grain, Guid InstanceId)> Start(WorkflowDefinition workflow)
    {
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.SetWorkflow(workflow);
        dynamic vars = new ExpandoObject();
        vars.txKey = CorrelationKey;
        await grain.SetInitialVariables(vars);
        await grain.StartWorkflow();
        return (grain, grain.GetPrimaryKey());
    }

    private async Task DeliverOuterComplete()
    {
        var key = MessageCorrelationKey.Build("trigger-outer-complete", CorrelationKey);
        var delivered = await Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(key)
            .DeliverMessage(new ExpandoObject());
        Assert.IsTrue(delivered, "trigger-outer-complete should be delivered to the outer catch event");
    }

    [TestMethod]
    public async Task ScenarioA_BothTransactionsCommit_HappyPath()
    {
        var (grain, instanceId) = await Start(BuildWorkflow("nested-tx-normal-inner", innerCancels: false));

        var waiting = await WaitForCondition(instanceId,
            s => s.ActiveActivityIds.Contains("trigger-outer-complete-catch"));
        CollectionAssert.IsSubsetOf(new[] { "inner-work", "inner-end", "inner-tx" },
            waiting.CompletedActivityIds.ToList());

        await DeliverOuterComplete();

        var final = await WaitForCondition(instanceId, s => s.IsCompleted);
        CollectionAssert.IsSubsetOf(
            new[] { "inner-work", "inner-end", "inner-tx", "trigger-outer-complete-catch", "outer-end", "outer-tx", "process-end" },
            final.CompletedActivityIds.ToList());
        CollectionAssert.DoesNotContain(final.CompletedActivityIds.ToList(), "inner-compensate",
            "Compensation must not run when both transactions commit");
        Assert.IsTrue(final.CompletedActivities.All(a => a.ErrorState is null));

        var outcomes = await grain.GetTransactionOutcomes();
        Assert.AreEqual(2, outcomes.Count);
        Assert.IsTrue(outcomes.Values.All(o => o.Outcome == TransactionOutcome.Completed));
    }

    [TestMethod]
    public async Task ScenarioB_InnerCancelsAndCompensates_OuterCommits()
    {
        var (grain, instanceId) = await Start(BuildWorkflow("nested-tx-cancel-inner", innerCancels: true));

        var waiting = await WaitForCondition(instanceId,
            s => s.ActiveActivityIds.Contains("trigger-outer-complete-catch"));
        CollectionAssert.IsSubsetOf(
            new[] { "inner-work", "inner-cancel-end", "inner-compensate", "inner-tx-cancel-boundary", "join-after-inner" },
            waiting.CompletedActivityIds.ToList());

        await DeliverOuterComplete();

        var final = await WaitForCondition(instanceId, s => s.IsCompleted);
        CollectionAssert.IsSubsetOf(
            new[] { "trigger-outer-complete-catch", "outer-end", "outer-tx", "process-end" },
            final.CompletedActivityIds.ToList());

        var outcomes = await grain.GetTransactionOutcomes();
        Assert.AreEqual(2, outcomes.Count);
        Assert.AreEqual(1, outcomes.Values.Count(o => o.Outcome == TransactionOutcome.Cancelled),
            "Inner transaction must record Cancelled");
        Assert.AreEqual(1, outcomes.Values.Count(o => o.Outcome == TransactionOutcome.Completed),
            "Outer transaction must record Completed");
    }

    [TestMethod]
    public async Task UncorrelatedOuterCatch_FailsCatchActivity_InsteadOfStallingInnerWork()
    {
        // #761 root cause: an intermediate message catch whose message has no correlation key
        // threw from the subscribe effect handler, escaping the script task's CompleteActivity
        // call and leaving `inner-work` permanently Active. It must now fail the catch event.
        var (_, instanceId) = await Start(
            BuildWorkflow("nested-tx-uncorrelated", innerCancels: false, withCorrelation: false));

        var snapshot = await WaitForCondition(instanceId,
            s => s.CompletedActivities.Any(a => a.ActivityId == "trigger-outer-complete-catch"));

        CollectionAssert.IsSubsetOf(new[] { "inner-work", "inner-end", "inner-tx" },
            snapshot.CompletedActivityIds.ToList());
        CollectionAssert.DoesNotContain(snapshot.ActiveActivityIds, "inner-work");
        var failedCatch = snapshot.CompletedActivities.Single(a => a.ActivityId == "trigger-outer-complete-catch");
        Assert.IsNotNull(failedCatch.ErrorState, "Catch event without a correlation key must be failed");
        StringAssert.Contains(failedCatch.ErrorState!.Message, "correlation key");
    }
}
