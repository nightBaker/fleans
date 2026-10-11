using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

[TestClass]
public class TimerStartEventSchedulerTests : WorkflowTestBase
{
    [TestMethod]
    public async Task FireTimerStartEvent_ShouldCreateAndStartWorkflowInstance()
    {
        // Arrange — deploy a workflow with TimerStartEvent
        var timerDef = new TimerDefinition(TimerType.Cycle, "R3/PT10M");
        var timerStart = new TimerStartEvent("timerStart1", timerDef);
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "scheduled-workflow",
            Activities = [timerStart, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", timerStart, task),
                new SequenceFlow("f2", task, end)
            ],
            ProcessDefinitionId = "scheduled-process:1:abc"
        };

        // Deploy the process definition so GetLatestWorkflowDefinition can find it
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("scheduled-workflow");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        // Act — call FireTimerStartEvent directly on the scheduler
        var scheduler = Cluster.GrainFactory.GetGrain<ITimerStartEventSchedulerGrain>("scheduled-workflow");
        var createdInstanceId = await scheduler.FireTimerStartEvent();

        // Assert — a workflow instance should have been created and started
        Assert.AreNotEqual(Guid.Empty, createdInstanceId);
        var snapshot = await QueryService.GetStateSnapshot(createdInstanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsStarted);
        // The workflow should have the TimerStartEvent completed and task1 active
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "Task1 should be active after timer start event completes");
    }

    [TestMethod]
    public async Task SubMinuteCycle_ShouldArmAndFireEachRepetition()
    {
        // Regression: the scheduler used the BPMN cycle interval as the Orleans reminder
        // period, which Orleans rejects below ReminderOptions.MinimumReminderPeriod (1 min),
        // so a timeCycle such as R2/PT2S failed to arm and never created instances.
        var timerStart = new TimerStartEvent("timerStart1", new TimerDefinition(TimerType.Cycle, "R2/PT2S"));
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sub-minute-cycle",
            Activities = [timerStart, end],
            SequenceFlows = [new SequenceFlow("f1", timerStart, end)],
            ProcessDefinitionId = "sub-minute-cycle:1:abc"
        };

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("sub-minute-cycle");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var count = 0;
        while (DateTime.UtcNow < deadline)
        {
            var page = await QueryService.GetInstancesByKey(
                "sub-minute-cycle", new Fleans.Application.QueryModels.PageRequest(1, 20, null, null));
            count = page.TotalCount;
            if (count >= 2) break;
            await Task.Delay(200);
        }

        Assert.AreEqual(2, count, "R2/PT2S should create exactly two instances.");
    }
}
