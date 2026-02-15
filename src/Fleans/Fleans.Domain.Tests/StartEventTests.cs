using Fleans.Application.QueryModels;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Domain.Tests;

[TestClass]
public class StartEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteActivity_AndStartWorkflow()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsStarted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "start" && a.IsCompleted));
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnFirstActivity_AfterStart()
    {
        // Arrange
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = new List<Activity> { start, task, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seq1", start, task),
                new SequenceFlow("seq2", task, end)
            }
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — after start event completes, "task" should be the active activity
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.HasCount(1, snapshot.ActiveActivities);
        Assert.AreEqual("task", snapshot.ActiveActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmptyList_WhenNoSequenceFlow()
    {
        // Arrange
        var start = new StartEvent("start");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = new List<Activity> { start },
            SequenceFlows = new List<SequenceFlow>()
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — start event completes but no next activity exists (no sequence flow)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "start"));
        Assert.HasCount(0, snapshot.ActiveActivities);
        Assert.IsFalse(snapshot.IsCompleted);
    }

    private static IWorkflowDefinition CreateSimpleWorkflow()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "test-workflow",
            Activities = new List<Activity> { start, task, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seq1", start, task),
                new SequenceFlow("seq2", task, end)
            }
        };
    }
}
