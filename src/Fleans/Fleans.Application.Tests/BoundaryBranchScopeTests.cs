using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// A non-interrupting boundary or escalation branch runs on a clone of the host's variables that no
/// join merges back. When the branch ends the clone is reduced to what the branch changed, so its
/// result stays visible without keeping a copy of the host's variables, or dropped if that is nothing (#861).
/// </summary>
[TestClass]
public class BoundaryBranchScopeTests : WorkflowTestBase
{
    private const string OrderId = "order-boundary-scope";

    private const string MessageName = "orderChanged";

    private static readonly MessageDefinition Message = new("msg1", MessageName, "orderId");

    /// <summary>start → task1 → end, with a non-interrupting message boundary on task1.</summary>
    private async Task<IWorkflowInstanceGrain> StartWithBoundaryBranch(
        string workflowId, List<Activity> branchActivities, Func<Activity, List<SequenceFlow>> branchFlows)
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");
        var boundary = new MessageBoundaryEvent("boundary", "task1", Message.Id, IsInterrupting: false);

        var workflow = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, task, end, boundary, .. branchActivities],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                .. branchFlows(boundary)
            ],
            Messages = [Message]
        };

        dynamic variables = new ExpandoObject();
        variables.orderId = OrderId;
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)variables);
        return instance;
    }

    private async Task FireBoundary()
    {
        var key = MessageCorrelationKey.Build(MessageName, OrderId);
        var delivered = await Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(key)
            .DeliverMessage(new ExpandoObject());
        Assert.IsTrue(delivered, "Message should be delivered");
    }

    private async Task<InstanceStateSnapshot> Snapshot(IWorkflowInstanceGrain instance) =>
        (await QueryService.GetStateSnapshot(instance.GetPrimaryKey()))!;

    private static bool HasScope(InstanceStateSnapshot snapshot, Guid scopeId) =>
        snapshot.VariableStates.Any(v => v.VariablesId == scopeId);

    private static List<string> NamesIn(InstanceStateSnapshot snapshot, Guid scopeId) =>
        snapshot.VariableStates.Single(v => v.VariablesId == scopeId).Variables.Keys.ToList();

    private static Guid ScopeOf(InstanceStateSnapshot snapshot, string activityId) =>
        snapshot.ActiveActivities.Concat(snapshot.CompletedActivities)
            .First(a => a.ActivityId == activityId).VariablesStateId;

    [TestMethod]
    public async Task BoundaryBranch_ChangedNothing_RemovesItsCloneAndKeepsTheHostScope()
    {
        var handler = new TaskActivity("handler");
        var handlerEnd = new EndEvent("handlerEnd");
        var instance = await StartWithBoundaryBranch("boundary-scope-completed", [handler, handlerEnd],
            boundary => [new SequenceFlow("b1", boundary, handler), new SequenceFlow("b2", handler, handlerEnd)]);

        await FireBoundary();

        var running = await Snapshot(instance);
        var cloneId = ScopeOf(running, "handler");
        var hostScopeId = ScopeOf(running, "task1");
        Assert.AreNotEqual(hostScopeId, cloneId, "The branch should run on a clone");
        Assert.IsTrue(HasScope(running, cloneId), "The clone stays while the branch runs");

        await instance.CompleteActivity("handler", new ExpandoObject());

        var afterBranch = await Snapshot(instance);
        Assert.IsFalse(afterBranch.IsCompleted, "The host is still running");
        Assert.IsFalse(HasScope(afterBranch, cloneId), "The clone should go when its branch ends");
        Assert.IsTrue(HasScope(afterBranch, hostScopeId), "The host's scope stays");

        await instance.CompleteActivity("task1", new ExpandoObject());

        var final = await Snapshot(instance);
        Assert.IsTrue(final.IsCompleted);
        Assert.IsTrue(final.CompletedActivities.Any(a => a.ActivityId == "end"));
        Assert.IsTrue(final.CompletedActivities.Any(a => a.ActivityId == "handlerEnd"));
    }

    [TestMethod]
    public async Task BoundaryBranch_WithResult_KeepsOnlyWhatItChanged()
    {
        var handler = new TaskActivity("handler");
        var handlerEnd = new EndEvent("handlerEnd");
        var instance = await StartWithBoundaryBranch("boundary-scope-result", [handler, handlerEnd],
            boundary => [new SequenceFlow("b1", boundary, handler), new SequenceFlow("b2", handler, handlerEnd)]);

        await FireBoundary();
        var running = await Snapshot(instance);
        var cloneId = ScopeOf(running, "handler");
        var hostScopeId = ScopeOf(running, "task1");
        CollectionAssert.Contains(NamesIn(running, cloneId), "orderId", "The clone starts as a full copy");

        dynamic variables = new ExpandoObject();
        variables.reminderSent = true;
        await instance.CompleteActivity("handler", (ExpandoObject)variables);

        var afterBranch = await Snapshot(instance);
        CollectionAssert.AreEqual(new[] { "reminderSent" }, NamesIn(afterBranch, cloneId));
        CollectionAssert.Contains(NamesIn(afterBranch, hostScopeId), "orderId", "The host's scope is untouched");

        await instance.CompleteActivity("task1", new ExpandoObject());

        var final = await Snapshot(instance);
        Assert.IsTrue(final.IsCompleted);
        CollectionAssert.AreEqual(new[] { "reminderSent" }, NamesIn(final, cloneId), "The result survives completion");
    }

    [TestMethod]
    public async Task BoundaryBranch_Failed_KeepsItsCloneForDiagnosis()
    {
        var handler = new TaskActivity("handler");
        var handlerEnd = new EndEvent("handlerEnd");
        var instance = await StartWithBoundaryBranch("boundary-scope-failed", [handler, handlerEnd],
            boundary => [new SequenceFlow("b1", boundary, handler), new SequenceFlow("b2", handler, handlerEnd)]);

        await FireBoundary();
        var cloneId = ScopeOf(await Snapshot(instance), "handler");

        await instance.FailActivity("handler", new Exception("boom"));

        var failed = await Snapshot(instance);
        var handlerEntry = failed.CompletedActivities.Single(a => a.ActivityId == "handler");
        Assert.IsNotNull(handlerEntry.ErrorState, "The handler should have failed");
        Assert.IsTrue(failed.ActiveActivities.Any(a => a.ActivityId == "task1"), "The host keeps running");
        CollectionAssert.Contains(NamesIn(failed, cloneId), "orderId", "A failed branch keeps all its variables for diagnosis");
    }

    [TestMethod]
    public async Task BoundaryBranch_WithForkAndJoin_KeepsItsCloneWholeUntilTheBranchEnds()
    {
        var fork = new ParallelGateway("fork", IsFork: true);
        var left = new TaskActivity("left");
        var right = new TaskActivity("right");
        var join = new ParallelGateway("join", IsFork: false);
        var afterJoin = new TaskActivity("afterJoin");
        var branchEnd = new EndEvent("branchEnd");
        var instance = await StartWithBoundaryBranch("boundary-scope-fork",
            [fork, left, right, join, afterJoin, branchEnd],
            boundary =>
            [
                new SequenceFlow("b1", boundary, fork),
                new SequenceFlow("b2", fork, left),
                new SequenceFlow("b3", fork, right),
                new SequenceFlow("b4", left, join),
                new SequenceFlow("b5", right, join),
                new SequenceFlow("b6", join, afterJoin),
                new SequenceFlow("b7", afterJoin, branchEnd)
            ]);

        await FireBoundary();
        var forking = await Snapshot(instance);
        var cloneId = ScopeOf(forking, "fork");

        // While both fork branches run nothing active uses the clone, but the join merges into it.
        dynamic leftVariables = new ExpandoObject();
        leftVariables.leftDone = true;
        await instance.CompleteActivity("left", (ExpandoObject)leftVariables);
        Assert.IsTrue(HasScope(await Snapshot(instance), cloneId), "The open fork still needs the clone");

        dynamic rightVariables = new ExpandoObject();
        rightVariables.rightDone = true;
        await instance.CompleteActivity("right", (ExpandoObject)rightVariables);

        var joined = await Snapshot(instance);
        Assert.AreEqual(cloneId, ScopeOf(joined, "afterJoin"), "The join should restore the clone");
        Assert.IsNotNull(await instance.GetVariable(cloneId, "leftDone"), "The join should merge into the clone");
        Assert.IsNotNull(await instance.GetVariable(cloneId, "rightDone"), "The join should merge into the clone");

        await instance.CompleteActivity("afterJoin", new ExpandoObject());

        var afterBranch = await Snapshot(instance);
        Assert.IsFalse(afterBranch.IsCompleted, "The host is still running");
        CollectionAssert.AreEquivalent(new[] { "leftDone", "rightDone" }, NamesIn(afterBranch, cloneId),
            "The clone should shrink to the branch's result when the whole branch ends");
    }

    [TestMethod]
    public async Task BoundaryBranch_WithSubProcess_KeepsItsCloneWholeUntilTheSubProcessEnds()
    {
        var innerStart = new StartEvent("innerStart");
        var innerTask = new TaskActivity("innerTask");
        var innerEnd = new EndEvent("innerEnd");
        var sub = new SubProcess("sub")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("i1", innerStart, innerTask),
                new SequenceFlow("i2", innerTask, innerEnd)
            ]
        };
        var branchEnd = new EndEvent("branchEnd");
        var instance = await StartWithBoundaryBranch("boundary-scope-sub", [sub, branchEnd],
            boundary => [new SequenceFlow("b1", boundary, sub), new SequenceFlow("b2", sub, branchEnd)]);

        await FireBoundary();
        var running = await Snapshot(instance);
        var cloneId = ScopeOf(running, "sub");
        Assert.IsTrue(HasScope(running, cloneId));

        dynamic variables = new ExpandoObject();
        variables.innerDone = true;
        await instance.CompleteActivity("innerTask", (ExpandoObject)variables);

        var afterBranch = await Snapshot(instance);
        Assert.IsTrue(afterBranch.CompletedActivities.Any(a => a.ActivityId == "branchEnd"), "The branch should end");
        CollectionAssert.AreEqual(new[] { "innerDone" }, NamesIn(afterBranch, cloneId), "The sub-process's result stays");
        Assert.IsFalse(HasScope(afterBranch, ScopeOf(afterBranch, "innerTask")), "The sub-process scope should go too");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task EscalationBranch_Ended_RemovesItsClone(bool isInterrupting)
    {
        var subStart = new StartEvent("sub_start");
        var escalationThrow = new EscalationIntermediateThrowEvent("escalation_throw", "ESC-1");
        var afterThrow = new TaskActivity("after_throw");
        var subEnd = new EndEvent("sub_end");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, escalationThrow, afterThrow, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("s1", subStart, escalationThrow),
                new SequenceFlow("s2", escalationThrow, afterThrow),
                new SequenceFlow("s3", afterThrow, subEnd)
            ]
        };
        var start = new StartEvent("start");
        var boundary = new EscalationBoundaryEvent("esc_boundary", "sub", "ESC-1", IsInterrupting: isInterrupting);
        var handler = new TaskActivity("handler");
        var boundaryEnd = new EndEvent("boundary_end");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = $"escalation-scope-{isInterrupting}",
            Activities = [start, sub, boundary, handler, boundaryEnd, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, end),
                new SequenceFlow("f3", boundary, handler),
                new SequenceFlow("f4", handler, boundaryEnd)
            ]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);

        var running = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivities.Any(a => a.ActivityId == "handler"));
        var cloneId = ScopeOf(running, "handler");
        Assert.IsTrue(HasScope(running, cloneId));

        await instance.CompleteActivity("handler", new ExpandoObject());

        var afterBranch = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.CompletedActivities.Any(a => a.ActivityId == "boundary_end"));
        Assert.IsFalse(HasScope(afterBranch, cloneId), "The clone should go when its branch ends");
    }
}
