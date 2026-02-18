using Fleans.Application.Grains;
using Fleans.Application.WorkflowFactory;
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
        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<placeholder/>");

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
}
