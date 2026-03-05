using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

public abstract class BoundaryEventTestBase : WorkflowTestBase
{
    protected abstract Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true);
    protected virtual List<MessageDefinition> GetMessageDefinitions() => [];
    protected virtual List<SignalDefinition> GetSignalDefinitions() => [];
    protected virtual Task SetupInitialState(IWorkflowInstanceGrain instance) => Task.CompletedTask;
    protected abstract Task TriggerBoundaryEvent(IWorkflowInstanceGrain instance, Guid hostInstanceId);

    [TestMethod]
    public async Task BoundaryEvent_EventArrivesFirst_ShouldFollowBoundaryFlow()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = CreateBoundaryEvent("boundary1", "task1");
        var end = new EndEvent("end");
        var recovery = new TaskActivity("recovery");
        var boundaryEnd = new EndEvent("boundaryEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-event-arrives-first",
            Activities = [start, task, boundary, end, recovery, boundaryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, recovery),
                new SequenceFlow("f4", recovery, boundaryEnd)
            ],
            Messages = GetMessageDefinitions(),
            Signals = GetSignalDefinitions()
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await SetupInitialState(instance);
        await instance.StartWorkflow();

        var instanceId = instance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "Task should be active");
        var hostInstanceId = preSnapshot.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        await TriggerBoundaryEvent(instance, hostInstanceId);

        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted, "Workflow should NOT be completed yet — recovery pending");
        var interruptedTask = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(interruptedTask, "Original task should be completed (interrupted)");
        Assert.IsTrue(interruptedTask.IsCancelled, "Interrupted task should be cancelled");
        Assert.IsNotNull(interruptedTask.CancellationReason, "Cancelled task should have a reason");
        Assert.IsNull(interruptedTask.ErrorState, "Cancelled task should not have error state");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "recovery"),
            "Recovery task should be active");

        await instance.CompleteActivity("recovery", new ExpandoObject());
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed after recovery");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "boundaryEnd"),
            "Should complete via boundary end");
    }

    [TestMethod]
    public async Task BoundaryEvent_TaskCompletesFirst_ShouldFollowNormalFlow()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = CreateBoundaryEvent("boundary1", "task1");
        var end = new EndEvent("end");
        var boundaryEnd = new EndEvent("boundaryEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-task-completes-first",
            Activities = [start, task, boundary, end, boundaryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, boundaryEnd)
            ],
            Messages = GetMessageDefinitions(),
            Signals = GetSignalDefinitions()
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await SetupInitialState(instance);
        await instance.StartWorkflow();

        await instance.CompleteActivity("task1", new ExpandoObject());

        var instanceId = instance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed via normal flow");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should complete via normal end event");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "boundaryEnd"),
            "Should NOT complete via boundary end event");
    }

    [TestMethod]
    public async Task NonInterruptingBoundary_AttachedActivityContinues()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = CreateBoundaryEvent("boundary1", "task1", isInterrupting: false);
        var afterBoundary = new TaskActivity("afterBoundary");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ni-boundary-test",
            Activities = [start, task, boundary, afterBoundary, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end1),
                new SequenceFlow("f3", boundary, afterBoundary),
                new SequenceFlow("f4", afterBoundary, end2)
            ],
            Messages = GetMessageDefinitions(),
            Signals = GetSignalDefinitions()
        };

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await SetupInitialState(instance);
        await instance.StartWorkflow();

        var instanceId = instance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        await TriggerBoundaryEvent(instance, hostInstanceId);

        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(midSnapshot!.IsCompleted, "Workflow should not be completed yet");
        Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "task1 should still be active after non-interrupting event");
        Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "afterBoundary"),
            "afterBoundary should be active on boundary path");
        Assert.IsFalse(midSnapshot.CompletedActivities.Any(a => a.ActivityId == "task1" && a.IsCancelled),
            "task1 should NOT be cancelled");

        await instance.CompleteActivity("task1", new ExpandoObject());

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");
        var task1Entry = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "task1");
        Assert.IsFalse(task1Entry.IsCancelled, "task1 should NOT be cancelled");
    }
}
