using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// A non-interrupting event sub-process that finishes while the main flow is inside another scope
/// (an embedded sub-process, parallel multi-instance iterations) must leave those scopes alone:
/// completing the handler removes only its own variable scope.
/// </summary>
[TestClass]
public class EventSubProcessSiblingScopeTests : WorkflowTestBase
{
    [TestMethod]
    [Ignore("Blocked by #840: completing a root event sub-process orphans every child scope of the root, including the running sub-process's (\"Variable state not found\").")]
    public async Task NonInterruptingEsp_CompletesWhileInsideASubProcess_TheSubProcessStillCompletes()
    {
        // Arrange — start → sub(subStart → subTask → subEnd) → end; root non-interrupting timer ESP
        var subStart = new StartEvent("subStart");
        var subTask = new TaskActivity("subTask");
        var subEnd = new EndEvent("subEnd");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, subTask, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", subStart, subTask),
                new SequenceFlow("sf2", subTask, subEnd)
            ]
        };
        var workflow = WithTimerEsp("esp-sibling-sub", sub);
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow);
        await WaitForCondition(instanceId, s => s.ActiveActivityIds.Contains("subTask"));

        // Act — the handler runs to its end, then the main flow continues inside the sub-process
        await instance.HandleTimerFired("espTimer", instanceId);
        await WaitForCondition(instanceId, s => s.CompletedActivityIds.Contains("espEnd"));
        await instance.CompleteActivity("subTask", new ExpandoObject());

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "sub");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    [Ignore("Blocked by #840: completing a root event sub-process orphans the parallel multi-instance iteration scopes (\"Variable state not found\").")]
    public async Task NonInterruptingEsp_CompletesWhileParallelIterationsRun_TheIterationsStillComplete()
    {
        // Arrange — start → MI task x2 → end; root non-interrupting timer ESP
        var mi = new MultiInstanceActivity("work", new TaskActivity("work"), IsSequential: false, LoopCardinality: 2);
        var workflow = WithTimerEsp("esp-sibling-mi", mi);
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceId = instance.GetPrimaryKey();
        await instance.StartWorkflow(workflow);
        var running = await WaitForCondition(instanceId, s => s.ActiveActivityIds.Count(id => id == "work") == 3);

        // Act
        await instance.HandleTimerFired("espTimer", instanceId);
        await WaitForCondition(instanceId, s => s.CompletedActivityIds.Contains("espEnd"));
        foreach (var iteration in running.ActiveActivities.Where(a => a.ActivityId == "work" && a.ActivityType != nameof(MultiInstanceActivity)))
            await instance.CompleteActivity("work", iteration.ActivityInstanceId, new ExpandoObject());

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
    }

    private static WorkflowDefinition WithTimerEsp(string id, Activity body)
    {
        var timerStart = new TimerStartEvent("espTimer", new TimerDefinition(TimerType.Duration, "PT30M"));
        var espEnd = new EndEvent("espEnd");
        var esp = new EventSubProcess("esp")
        {
            Activities = [timerStart, espEnd],
            SequenceFlows = [new SequenceFlow("esp1", timerStart, espEnd)],
            IsInterrupting = false
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = id,
            Activities = [start, body, end, esp],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, body),
                new SequenceFlow("f2", body, end)
            ]
        };
    }
}
