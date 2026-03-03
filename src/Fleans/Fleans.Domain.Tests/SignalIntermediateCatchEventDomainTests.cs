using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class SignalIntermediateCatchEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallExecute_RegisterSignalCommand_AndNotComplete()
    {
        // Arrange
        var sigCatch = new SignalIntermediateCatchEvent("sigCatch1", "sig1");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateDefinitionWithSignal(
            [sigCatch, end],
            [new SequenceFlow("seq1", sigCatch, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("sigCatch1");

        // Act
        var commands = await sigCatch.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — should execute and register, but NOT complete (waits for signal delivery)
        await activityContext.Received(1).Execute();
        await activityContext.DidNotReceive().Complete();
        var sigCmd = commands.OfType<RegisterSignalCommand>().Single();
        Assert.AreEqual("order_shipped", sigCmd.SignalName);
        Assert.AreEqual("sigCatch1", sigCmd.ActivityId);
        Assert.IsFalse(sigCmd.IsBoundary);
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("sigCatch1", executedEvent.activityId);
        Assert.AreEqual("SignalIntermediateCatchEvent", executedEvent.TypeName);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var sigCatch = new SignalIntermediateCatchEvent("sigCatch1", "sig1");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateDefinitionWithSignal(
            [sigCatch, end],
            [new SequenceFlow("seq1", sigCatch, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sigCatch1");

        // Act
        var nextActivities = await sigCatch.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].NextActivity.ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoOutgoingFlow()
    {
        // Arrange
        var sigCatch = new SignalIntermediateCatchEvent("sigCatch1", "sig1");
        var definition = ActivityTestHelper.CreateDefinitionWithSignal([sigCatch], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sigCatch1");

        // Act
        var nextActivities = await sigCatch.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(0, nextActivities);
    }
}
