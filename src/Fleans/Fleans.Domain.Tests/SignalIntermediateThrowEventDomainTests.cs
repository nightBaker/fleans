using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class SignalIntermediateThrowEventDomainTests
{
    private static WorkflowDefinition CreateDefinitionWithSignal(
        List<Activity> activities,
        List<SequenceFlow> sequenceFlows,
        string signalId = "sig1",
        string signalName = "order_shipped")
    {
        return new WorkflowDefinition
        {
            WorkflowId = "test-workflow",
            Activities = activities,
            SequenceFlows = sequenceFlows,
            Signals = [new SignalDefinition(signalId, signalName)]
        };
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldThrowSignalCommand_AndComplete()
    {
        // Arrange
        var sigThrow = new SignalIntermediateThrowEvent("sigThrow1", "sig1");
        var end = new EndEvent("end");
        var definition = CreateDefinitionWithSignal(
            [sigThrow, end],
            [new SequenceFlow("seq1", sigThrow, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("sigThrow1");

        // Act
        var commands = await sigThrow.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — throw event emits the signal and completes immediately
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var throwCmd = commands.OfType<ThrowSignalCommand>().Single();
        Assert.AreEqual("order_shipped", throwCmd.SignalName);
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("sigThrow1", executedEvent.activityId);
        Assert.AreEqual("SignalIntermediateThrowEvent", executedEvent.TypeName);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var sigThrow = new SignalIntermediateThrowEvent("sigThrow1", "sig1");
        var end = new EndEvent("end");
        var definition = CreateDefinitionWithSignal(
            [sigThrow, end],
            [new SequenceFlow("seq1", sigThrow, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sigThrow1");

        // Act
        var nextActivities = await sigThrow.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoOutgoingFlow()
    {
        // Arrange
        var sigThrow = new SignalIntermediateThrowEvent("sigThrow1", "sig1");
        var definition = CreateDefinitionWithSignal([sigThrow], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sigThrow1");

        // Act
        var nextActivities = await sigThrow.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(0, nextActivities);
    }
}
