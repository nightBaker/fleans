using Fleans.Application.QueryModels;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Domain.Tests;

[TestClass]
public class EndEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteActivity_AndCompleteWorkflow()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — complete the task so EndEvent fires
        var variables = new ExpandoObject();
        await workflowInstance.CompleteActivity("task", variables);

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        var endActivity = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityType == "EndEvent");

        Assert.IsNotNull(endActivity);
        Assert.IsTrue(endActivity.IsCompleted);
        Assert.IsTrue(snapshot.IsCompleted);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldAlwaysReturnEmptyList()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — complete the task so EndEvent executes
        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        // Assert — workflow is completed and no active activities remain,
        // proving EndEvent has no next activities
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.HasCount(0, snapshot.ActiveActivities);
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
