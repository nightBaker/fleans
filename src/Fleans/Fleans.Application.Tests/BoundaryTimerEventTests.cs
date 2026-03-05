using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class BoundaryTimerEventTests : BoundaryEventTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new BoundaryTimerEvent(boundaryId, attachedToId,
            new TimerDefinition(TimerType.Duration, "PT30M"), IsInterrupting: isInterrupting);

    protected override async Task TriggerBoundaryEvent(IWorkflowInstanceGrain instance, Guid hostInstanceId)
        => await instance.HandleTimerFired("boundary1", hostInstanceId);

    [TestMethod]
    public async Task InterruptingBoundaryTimer_StillCancelsAttachedActivity()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var afterTimer = new TaskActivity("afterTimer");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "i-timer-regression",
            Activities = [start, task, boundaryTimer, afterTimer, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end1),
                new SequenceFlow("f3", boundaryTimer, afterTimer),
                new SequenceFlow("f4", afterTimer, end2)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        await workflowInstance.HandleTimerFired("bt1", hostInstanceId);

        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        var task1Entry = snapshot!.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(task1Entry, "task1 should be completed (interrupted)");
        Assert.IsTrue(task1Entry.IsCancelled, "task1 should be cancelled by interrupting timer");
    }
}
