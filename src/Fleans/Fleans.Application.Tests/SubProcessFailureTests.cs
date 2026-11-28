using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

/// <summary>
/// Where the failure of a sub-process goes once nothing inside it caught the error (#1144): to the
/// multi-instance host, the enclosing sub-process, the parent's call activity, or the instance.
/// </summary>
[TestClass]
public class SubProcessFailureTests : WorkflowTestBase
{
    private static ScriptTask AutoTask(string id) => new(id, "PASS", "csharp");

    /// <summary>start → work (fails) → end, as a sub-process.</summary>
    private static SubProcess FailingSubProcess(string id)
    {
        var start = new StartEvent($"{id}_start");
        var work = new ScriptTask($"{id}_work", "FAIL", "csharp");
        var end = new EndEvent($"{id}_end");
        return new SubProcess(id)
        {
            Activities = [start, work, end],
            SequenceFlows = [new($"{id}_f1", start, work), new($"{id}_f2", work, end)]
        };
    }

    /// <summary>start → host → end, with an optional catch-all error boundary on the host → fix → fixEnd.</summary>
    private static WorkflowDefinition Around(string workflowId, Activity host, string? boundaryErrorCode = "none")
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, host, end],
            SequenceFlows = [new("f1", start, host), new("f2", host, end)]
        };
        if (boundaryErrorCode == "none") return workflow;

        var onError = new BoundaryErrorEvent("onError", host.ActivityId, boundaryErrorCode);
        var fix = AutoTask("fix");
        var fixEnd = new EndEvent("fixEnd");
        workflow.Activities.AddRange([onError, fix, fixEnd]);
        workflow.SequenceFlows.AddRange([new("f3", onError, fix), new("f4", fix, fixEnd)]);
        return workflow;
    }

    private async Task<InstanceStateSnapshot> RunToEnd(IWorkflowDefinition workflow)
    {
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        return await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);
    }

    [TestMethod]
    public async Task MultiInstanceSubProcess_WhoseIterationFails_FailsTheInstance()
    {
        var mi = new MultiInstanceActivity("mi", FailingSubProcess("mi"), IsSequential: false, LoopCardinality: 2);

        var snapshot = await RunToEnd(Around("mi-sub-process-fails", mi));

        Assert.IsTrue(snapshot.IsFailed);
        Assert.IsEmpty(snapshot.ActiveActivities, "no iteration and no host stays open");
    }

    [TestMethod]
    public async Task MultiInstanceSubProcess_WhoseIterationFails_IsCaughtByTheBoundaryOnItsHost()
    {
        var mi = new MultiInstanceActivity("mi", FailingSubProcess("mi"), IsSequential: true, LoopCardinality: 2);

        var snapshot = await RunToEnd(Around("mi-sub-process-caught", mi, boundaryErrorCode: null));

        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "fix"), "the boundary fires once");
    }

    /// <summary>start → tx [ work (fails) ] → end, as a sub-process: the Transaction ends in Hazard and fails.</summary>
    private static SubProcess SubProcessAroundAFailingTransaction()
    {
        var txStart = new StartEvent("txStart");
        var txWork = new ScriptTask("txWork", "FAIL", "csharp");
        var txEnd = new EndEvent("txEnd");
        var tx = new Transaction("tx")
        {
            Activities = [txStart, txWork, txEnd],
            SequenceFlows = [new("t1", txStart, txWork), new("t2", txWork, txEnd)]
        };
        var subStart = new StartEvent("subStart");
        var subEnd = new EndEvent("subEnd");
        return new SubProcess("sub")
        {
            Activities = [subStart, tx, subEnd],
            SequenceFlows = [new("s1", subStart, tx), new("s2", tx, subEnd)]
        };
    }

    [TestMethod]
    public async Task FailedTransaction_InsideASubProcess_FailsTheInstance()
    {
        var snapshot = await RunToEnd(Around("failed-tx-in-sub-process", SubProcessAroundAFailingTransaction()));

        Assert.IsTrue(snapshot.IsFailed);
        Assert.IsEmpty(snapshot.ActiveActivities);
    }

    [TestMethod]
    public async Task FailedTransaction_InsideASubProcess_IsCaughtByTheBoundaryOnTheSubProcess()
    {
        var snapshot = await RunToEnd(
            Around("failed-tx-in-sub-process-caught", SubProcessAroundAFailingTransaction(), boundaryErrorCode: null));

        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "fix"), "the boundary fires once");
    }

    [TestMethod]
    public async Task FailedSubProcess_OnOneBranch_DoesNotEndTheInstance_WhileAnotherBranchIsActive()
    {
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var sub = FailingSubProcess("sub");
        var subDone = new EndEvent("subDone");
        var review = new TaskActivity("review");
        var reviewDone = new EndEvent("reviewDone");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "failed-sub-process-parallel",
            Activities = [start, fork, sub, subDone, review, reviewDone],
            SequenceFlows =
            [
                new("f1", start, fork), new("f2", fork, sub), new("f3", sub, subDone),
                new("f4", fork, review), new("f5", review, reviewDone)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(),
            s => s.CompletedActivities.Any(a => a.ActivityId == "sub"));

        Assert.IsFalse(snapshot.IsCompleted, "'review' is still active");
        Assert.AreEqual("review", snapshot.ActiveActivities.Single().ActivityId);
    }

    [TestMethod]
    public async Task FailedSubProcess_InAChildProcess_FailsTheCallActivity_WithTheRethrownErrorCode()
    {
        // Child: sub [ work (fails, 500) → caught inside → error end event "PAYMENT_FAILED" ].
        // Nothing in the child catches the rethrown error, so the sub-process fails with it.
        var subStart = new StartEvent("subStart");
        var work = new ScriptTask("work", "FAIL", "csharp");
        var subEnd = new EndEvent("subEnd");
        var caught = new BoundaryErrorEvent("caught", "work", null);
        var rethrow = new ErrorEndEvent("rethrow", "PAYMENT_FAILED");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, work, subEnd, caught, rethrow],
            SequenceFlows = [new("s1", subStart, work), new("s2", work, subEnd), new("s3", caught, rethrow)]
        };
        var child = Around("sub-process-failure-child", sub);
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(child.WorkflowId).DeployVersion(child, "<xml/>");

        var call = new CallActivity("call", child.WorkflowId, [], []);
        var parent = Around("sub-process-failure-parent", call, boundaryErrorCode: "PAYMENT_FAILED");
        var parentDefinition = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(parent.WorkflowId);
        await parentDefinition.DeployVersion(parent, "<xml/>");

        var snapshot = await RunToEnd(await parentDefinition.GetLatestDefinition());

        Assert.IsFalse(snapshot.IsFailed, "the parent's boundary for PAYMENT_FAILED catches the child's failure");
        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "fix"),
            "the parent is told once, with the code of the error that left the sub-process");
    }
}
