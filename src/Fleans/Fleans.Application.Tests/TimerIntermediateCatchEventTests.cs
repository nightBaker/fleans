using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class TimerIntermediateCatchEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task TimerIntermediateCatch_ShouldSuspendWorkflow_UntilReminderFires()
    {
        // Arrange — Start → Timer(PT5M) → End
        var start = new StartEvent("start");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var timer = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-test",
            Activities = [start, timer, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timer),
                new SequenceFlow("f2", timer, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — workflow should be suspended at timer (timer is active, not completed)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted, "Workflow should NOT be completed — timer is waiting");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "timer1"),
            "Timer activity should be active");
    }

    [TestMethod]
    public async Task TimerIntermediateCatch_ShouldComplete_WhenReminderSimulated()
    {
        // Arrange — Start → Timer(PT5M) → End
        var start = new StartEvent("start");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var timer = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-test",
            Activities = [start, timer, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timer),
                new SequenceFlow("f2", timer, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — simulate reminder firing by calling CompleteActivity
        await workflowInstance.CompleteActivity("timer1", new ExpandoObject());

        // Assert — workflow should now be completed
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed after timer fires");
    }

    [TestMethod]
    public async Task TimerIntermediateCatch_BetweenTasks_ShouldPreserveVariables()
    {
        // Arrange — Start → Task → Timer → End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT1M");
        var timer = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-vars-test",
            Activities = [start, task, timer, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, timer),
                new SequenceFlow("f3", timer, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task with variables
        dynamic vars = new ExpandoObject();
        vars.result = "done";
        await workflowInstance.CompleteActivity("task1", vars);

        // Timer is now active — simulate reminder
        await workflowInstance.CompleteActivity("timer1", new ExpandoObject());

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsTrue(snapshot.VariableStates.Count > 0, "Variables should be preserved across timer");
    }

    [TestMethod]
    public async Task TimerIntermediateCatch_HandleTimerFired_ShouldCompleteWorkflow()
    {
        // Arrange — Start → Timer(PT5M) → End
        var start = new StartEvent("start");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var timer = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-handle-fired-test",
            Activities = [start, timer, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timer),
                new SequenceFlow("f2", timer, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — simulate timer callback via HandleTimerFired
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var timerInstanceId = preSnapshot!.ActiveActivities.First(a => a.ActivityId == "timer1").ActivityInstanceId;
        await workflowInstance.HandleTimerFired("timer1", timerInstanceId);
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed after HandleTimerFired");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should complete via end event");
    }
}
