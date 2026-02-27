using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class MessageBoundaryEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately()
    {
        // Arrange
        var boundary = new MessageBoundaryEvent("bmsg1", "task1", "msg_payment");
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundary, recovery],
            [new SequenceFlow("seq1", boundary, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("bmsg1");

        // Act
        var commands = await boundary.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — boundary completes immediately
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("bmsg1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var boundary = new MessageBoundaryEvent("bmsg1", "task1", "msg_payment");
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundary, recovery],
            [new SequenceFlow("seq1", boundary, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("bmsg1");

        // Act
        var nextActivities = await boundary.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.AreEqual(1, nextActivities.Count);
        Assert.AreEqual("recovery", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public void MessageBoundaryEvent_ShouldHaveCorrectProperties()
    {
        var boundary = new MessageBoundaryEvent("bmsg1", "task1", "msg_payment");
        Assert.AreEqual("bmsg1", boundary.ActivityId);
        Assert.AreEqual("task1", boundary.AttachedToActivityId);
        Assert.AreEqual("msg_payment", boundary.MessageDefinitionId);
    }
}
