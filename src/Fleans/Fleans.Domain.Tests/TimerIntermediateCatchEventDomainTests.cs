using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class TimerIntermediateCatchEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallExecute_AndPublishEvent_ButNotComplete()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var timerEvent = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [timerEvent, end],
            [new SequenceFlow("seq1", timerEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("timer1");

        // Act
        var commands = await timerEvent.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — should execute but NOT complete (waits for reminder)
        await activityContext.Received(1).Execute();
        await activityContext.DidNotReceive().Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("timer1", executedEvent.activityId);
        Assert.AreEqual("TimerIntermediateCatchEvent", executedEvent.TypeName);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var timerEvent = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [timerEvent, end],
            [new SequenceFlow("seq1", timerEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("timer1");

        // Act
        var nextActivities = await timerEvent.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }
}
