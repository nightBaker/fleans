using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class StartEventActivityTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallComplete_OnActivityContext()
    {
        // Arrange
        var startEvent = new StartEvent("start");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [startEvent, end],
            [new SequenceFlow("seq1", startEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("start");

        // Act
        var commands = await startEvent.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_WhenSequenceFlowExists()
    {
        // Arrange
        var startEvent = new StartEvent("start");
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [startEvent, task],
            [new SequenceFlow("seq1", startEvent, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("start");

        // Act
        var nextActivities = await startEvent.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("task1", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoSequenceFlow()
    {
        // Arrange
        var startEvent = new StartEvent("start");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [startEvent],
            []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("start");

        // Act
        var nextActivities = await startEvent.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(0, nextActivities);
    }
}
