using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class MessageStartEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately_LikeStartEvent()
    {
        // Arrange
        var messageStart = new MessageStartEvent("msgStart1", "msg_def_1");
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [messageStart, task],
            [new SequenceFlow("seq1", messageStart, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("msgStart1");

        // Act
        var commands = await messageStart.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("msgStart1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var messageStart = new MessageStartEvent("msgStart1", "msg_def_1");
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [messageStart, task],
            [new SequenceFlow("seq1", messageStart, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("msgStart1");

        // Act
        var nextActivities = await messageStart.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("task1", nextActivities[0].NextActivity.ActivityId);
    }
}
