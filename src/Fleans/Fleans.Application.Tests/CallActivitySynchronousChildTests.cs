using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Diagnostics;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// #838: a called process without a wait state finishes (completes, fails or escalates) while the
/// parent is still inside its own turn, awaiting the child's start. The parent's callbacks must not
/// wait for that turn to end, or both sides block until the 30 s response timeout and the call
/// activity fails. Each test bounds the start well under that timeout.
/// </summary>
[TestClass]
public class CallActivitySynchronousChildTests : WorkflowTestBase
{
    private static readonly TimeSpan StartBound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task CallActivity_ChildWithoutWaitState_CompletesParent()
    {
        // Arrange — child: start → end; parent: start → call1 → end
        var childStart = new StartEvent("childStart");
        var childEnd = new EndEvent("childEnd");
        await DeployChild(new WorkflowDefinition
        {
            WorkflowId = "syncChildCompletes",
            Activities = [childStart, childEnd],
            SequenceFlows = [new SequenceFlow("cs1", childStart, childEnd)]
        });

        var start = new StartEvent("start");
        var call1 = new CallActivity("call1", "syncChildCompletes", [], []);
        var end = new EndEvent("end");
        var parent = await DeployParent(new WorkflowDefinition
        {
            WorkflowId = "syncChildCompletesParent",
            Activities = [start, call1, end],
            SequenceFlows =
            [
                new SequenceFlow("ps1", start, call1),
                new SequenceFlow("ps2", call1, end)
            ]
        });

        // Act
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var elapsed = await StartBounded(instance, parent);

        // Assert — completion state
        var snapshot = await PollForNoActiveActivities(instance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Parent should complete once the child completes");
        Assert.IsFalse(snapshot.IsFailed, "The call activity must not fail on a response timeout");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "call1");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
        Assert.IsTrue(elapsed < StartBound, $"StartWorkflow took {elapsed}");
    }

    [TestMethod]
    public async Task CallActivity_ChildFailingWithoutWaitState_RoutesToErrorBoundary()
    {
        // Arrange — child: start → task (input mapping that does not evaluate) → end
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask")
        {
            InputMappings = [new InputMapping("=missing.Total * 2", "doubled")]
        };
        var childEnd = new EndEvent("childEnd");
        await DeployChild(new WorkflowDefinition
        {
            WorkflowId = "syncChildFails",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cs1", childStart, childTask),
                new SequenceFlow("cs2", childTask, childEnd)
            ]
        });

        // parent: start → call1 → happyEnd, error boundary on call1 → errorHandler → end2
        var start = new StartEvent("start");
        var call1 = new CallActivity("call1", "syncChildFails", [], []);
        var happyEnd = new EndEvent("happyEnd");
        var errorBoundary = new BoundaryErrorEvent("errorBoundary", "call1", null);
        var errorHandler = new TaskActivity("errorHandler");
        var end2 = new EndEvent("end2");
        var parent = await DeployParent(new WorkflowDefinition
        {
            WorkflowId = "syncChildFailsParent",
            Activities = [start, call1, happyEnd, errorBoundary, errorHandler, end2],
            SequenceFlows =
            [
                new SequenceFlow("ps1", start, call1),
                new SequenceFlow("ps2", call1, happyEnd),
                new SequenceFlow("ps3", errorBoundary, errorHandler),
                new SequenceFlow("ps4", errorHandler, end2)
            ]
        });

        // Act
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var elapsed = await StartBounded(instance, parent);

        // Assert — failure state: the child's failure, not a timeout, fired the boundary
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivities.Any(a => a.ActivityId == "errorHandler"));
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "errorBoundary"));
        var call = snapshot.CompletedActivities.Single(a => a.ActivityId == "call1");
        Assert.IsNotNull(call.ErrorState, "The call activity should carry the child's failure");
        StringAssert.Contains(call.ErrorState.Message, "Input mapping");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "happyEnd"));
        Assert.IsTrue(elapsed < StartBound, $"StartWorkflow took {elapsed}");

        await instance.CompleteActivity("errorHandler", new ExpandoObject());
        var final = await PollForNoActiveActivities(instance.GetPrimaryKey());
        Assert.IsTrue(final!.IsCompleted);
        CollectionAssert.Contains(final.CompletedActivityIds, "end2");
    }

    [TestMethod]
    public async Task CallActivity_ChildEscalatingWithoutWaitState_InterruptingBoundaryRoutesToRecovery()
    {
        // Arrange — child: start → escalation throw → end
        var childStart = new StartEvent("childStart");
        var childThrow = new EscalationIntermediateThrowEvent("childEscThrow", "ESC-SYNC");
        var childEnd = new EndEvent("childEnd");
        await DeployChild(new WorkflowDefinition
        {
            WorkflowId = "syncChildEscalates",
            Activities = [childStart, childThrow, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cs1", childStart, childThrow),
                new SequenceFlow("cs2", childThrow, childEnd)
            ]
        });

        // parent: start → call1 → normalEnd, interrupting escalation boundary → recoveryTask → recoveryEnd
        var start = new StartEvent("start");
        var call1 = new CallActivity("call1", "syncChildEscalates", [], []);
        var normalEnd = new EndEvent("normalEnd");
        var escBoundary = new EscalationBoundaryEvent("escBoundary", "call1", "ESC-SYNC", IsInterrupting: true);
        var recoveryTask = new TaskActivity("recoveryTask");
        var recoveryEnd = new EndEvent("recoveryEnd");
        var parent = await DeployParent(new WorkflowDefinition
        {
            WorkflowId = "syncChildEscalatesParent",
            Activities = [start, call1, normalEnd, escBoundary, recoveryTask, recoveryEnd],
            SequenceFlows =
            [
                new SequenceFlow("ps1", start, call1),
                new SequenceFlow("ps2", call1, normalEnd),
                new SequenceFlow("ps3", escBoundary, recoveryTask),
                new SequenceFlow("ps4", recoveryTask, recoveryEnd)
            ]
        });

        // Act
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var elapsed = await StartBounded(instance, parent);

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivities.Any(a => a.ActivityId == "recoveryTask"));
        Assert.IsFalse(snapshot.IsFailed);
        var call = snapshot.CompletedActivities.Single(a => a.ActivityId == "call1");
        Assert.IsTrue(call.IsCancelled, "The interrupting boundary should cancel the call activity");
        Assert.IsNull(call.ErrorState, "The call activity must not fail on a response timeout");
        Assert.IsTrue(elapsed < StartBound, $"StartWorkflow took {elapsed}");

        await instance.CompleteActivity("recoveryTask", new ExpandoObject());
        var final = await PollForNoActiveActivities(instance.GetPrimaryKey());
        Assert.IsTrue(final!.IsCompleted);
        CollectionAssert.Contains(final.CompletedActivityIds, "recoveryEnd");
        Assert.IsFalse(final.CompletedActivityIds.Contains("normalEnd"));
    }

    [TestMethod]
    public async Task SignalThrownAndCaughtBySameInstance_DoesNotStall()
    {
        // Arrange — start → fork → [catch sig → end1] + [task t → throw sig → end2]
        var signalDef = new SignalDefinition("sig1", "selfCaughtSignal-838");
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var catchSignal = new SignalIntermediateCatchEvent("catchSig", "sig1");
        var end1 = new EndEvent("end1");
        var task = new TaskActivity("t");
        var throwSignal = new SignalIntermediateThrowEvent("throwSig", "sig1");
        var end2 = new EndEvent("end2");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "selfCaughtSignal",
            Activities = [start, fork, catchSignal, end1, task, throwSignal, end2],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, fork),
                new SequenceFlow("f2", fork, catchSignal),
                new SequenceFlow("f3", catchSignal, end1),
                new SequenceFlow("f4", fork, task),
                new SequenceFlow("f5", task, throwSignal),
                new SequenceFlow("f6", throwSignal, end2)
            ],
            Signals = [signalDef]
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);

        // Act
        var sw = Stopwatch.StartNew();
        await instance.CompleteActivity("t", new ExpandoObject()).WaitAsync(StartBound);
        sw.Stop();

        // Assert
        var snapshot = await PollForNoActiveActivities(instance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end1");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end2");
        Assert.IsTrue(sw.Elapsed < StartBound, $"CompleteActivity took {sw.Elapsed}");
    }

    private async Task DeployChild(WorkflowDefinition child) =>
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(child.WorkflowId)
            .DeployVersion(child, "<xml/>");

    private async Task<IWorkflowDefinition> DeployParent(WorkflowDefinition parent)
    {
        var grain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(parent.WorkflowId);
        await grain.DeployVersion(parent, "<xml/>");
        return await grain.GetLatestDefinition();
    }

    /// <summary>Starts the instance, failing fast (instead of after 30 s) when the start deadlocks.</summary>
    private static async Task<TimeSpan> StartBounded(IWorkflowInstanceGrain instance, IWorkflowDefinition definition)
    {
        var sw = Stopwatch.StartNew();
        await instance.StartWorkflow(definition).WaitAsync(StartBound);
        return sw.Elapsed;
    }
}
