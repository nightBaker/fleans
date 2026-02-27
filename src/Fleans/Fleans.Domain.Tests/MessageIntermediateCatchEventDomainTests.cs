using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class MessageIntermediateCatchEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallExecute_AndRegisterSubscription_ButNotComplete()
    {
        // Arrange
        var msgEvent = new MessageIntermediateCatchEvent("msgCatch1", "msg_payment");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [msgEvent, end],
            [new SequenceFlow("seq1", msgEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("msgCatch1");

        // Act
        var commands = await msgEvent.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — should execute and register, but NOT complete
        await activityContext.Received(1).Execute();
        await activityContext.DidNotReceive().Complete();
        var msgCmd = commands.OfType<RegisterMessageCommand>().Single();
        Assert.AreEqual("msg_payment", msgCmd.MessageDefinitionId);
        Assert.AreEqual("msgCatch1", msgCmd.ActivityId);
        Assert.IsFalse(msgCmd.IsBoundary);
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("msgCatch1", executedEvent.activityId);
        Assert.AreEqual("MessageIntermediateCatchEvent", executedEvent.TypeName);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var msgEvent = new MessageIntermediateCatchEvent("msgCatch1", "msg_payment");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [msgEvent, end],
            [new SequenceFlow("seq1", msgEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("msgCatch1");

        // Act
        var nextActivities = await msgEvent.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.AreEqual(1, nextActivities.Count);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }
}
