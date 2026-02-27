using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class EventBasedGatewayActivityTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallComplete()
    {
        // Arrange
        var gateway = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timer1", new TimerDefinition(TimerType.Duration, "PT1H"));
        var msgCatch = new MessageIntermediateCatchEvent("msg1", "msgDef1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, timerCatch, msgCatch],
            [
                new SequenceFlow("f1", gateway, timerCatch),
                new SequenceFlow("f2", gateway, msgCatch)
            ]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("ebg1");

        // Act
        var commands = await gateway.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        Assert.IsTrue(commands.OfType<CompleteCommand>().Any());
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnAllOutgoingFlowTargets()
    {
        // Arrange
        var gateway = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timer1", new TimerDefinition(TimerType.Duration, "PT1H"));
        var msgCatch = new MessageIntermediateCatchEvent("msg1", "msgDef1");
        var sigCatch = new SignalIntermediateCatchEvent("sig1", "sigDef1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, timerCatch, msgCatch, sigCatch],
            [
                new SequenceFlow("f1", gateway, timerCatch),
                new SequenceFlow("f2", gateway, msgCatch),
                new SequenceFlow("f3", gateway, sigCatch)
            ]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("ebg1");

        // Act
        var nextActivities = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(3, nextActivities);
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "timer1"));
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "msg1"));
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "sig1"));
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldReturnCompleteCommand_NotCallCompleteDirectly()
    {
        var gateway = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timer1", new TimerDefinition(TimerType.Duration, "PT1H"));
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, timerCatch],
            [new SequenceFlow("f1", gateway, timerCatch)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("ebg1");

        var commands = await gateway.ExecuteAsync(workflowContext, activityContext, definition);

        // Activities return CompleteCommand instead of calling Complete() directly
        Assert.IsTrue(commands.OfType<CompleteCommand>().Any());
    }
}
