using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class TaskActivityDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallExecute_AndPublishActivityExecutedEvent()
    {
        // Arrange
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task, end],
            [new SequenceFlow("seq1", task, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("task1");

        // Act
        await task.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Execute();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("task1", executedEvent.activityId);
        Assert.AreEqual("TaskActivity", executedEvent.TypeName);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnSingleTarget_ViaSequenceFlow()
    {
        // Arrange
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task, end],
            [new SequenceFlow("seq1", task, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1");

        // Act
        var nextActivities = await task.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoOutgoingFlow()
    {
        // Arrange
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task],
            []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1");

        // Act
        var nextActivities = await task.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(0, nextActivities);
    }
}
