using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class TimerStartEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately_LikeStartEvent()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Cycle, "R3/PT10M");
        var timerStart = new TimerStartEvent("timerStart1", timerDef);
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [timerStart, task],
            [new SequenceFlow("seq1", timerStart, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("timerStart1");

        // Act
        var commands = await timerStart.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Execute();
        Assert.IsTrue(commands.OfType<CompleteCommand>().Any());
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("timerStart1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Cycle, "R3/PT10M");
        var timerStart = new TimerStartEvent("timerStart1", timerDef);
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [timerStart, task],
            [new SequenceFlow("seq1", timerStart, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("timerStart1");

        // Act
        var nextActivities = await timerStart.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("task1", nextActivities[0].ActivityId);
    }
}
