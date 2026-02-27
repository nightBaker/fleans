using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class BoundaryTimerEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundaryTimer, recovery],
            [new SequenceFlow("seq1", boundaryTimer, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("bt1");

        // Act
        var commands = await boundaryTimer.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("bt1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundaryTimer, recovery],
            [new SequenceFlow("seq1", boundaryTimer, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("bt1");

        // Act
        var nextActivities = await boundaryTimer.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("recovery", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public void BoundaryTimerEvent_ShouldHaveCorrectProperties()
    {
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundary = new BoundaryTimerEvent("bt1", "task1", timerDef);

        Assert.AreEqual("bt1", boundary.ActivityId);
        Assert.AreEqual("task1", boundary.AttachedToActivityId);
        Assert.AreEqual(TimerType.Duration, boundary.TimerDefinition.Type);
        Assert.AreEqual("PT30M", boundary.TimerDefinition.Expression);
    }
}
