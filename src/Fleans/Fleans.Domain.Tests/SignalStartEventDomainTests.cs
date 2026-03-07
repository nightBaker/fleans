using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class SignalStartEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately_LikeStartEvent()
    {
        // Arrange
        var signalStart = new SignalStartEvent("sigStart1", "sig_def_1");
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [signalStart, task],
            [new SequenceFlow("seq1", signalStart, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("sigStart1");

        // Act
        var commands = await signalStart.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("sigStart1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var signalStart = new SignalStartEvent("sigStart1", "sig_def_1");
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [signalStart, task],
            [new SequenceFlow("seq1", signalStart, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sigStart1");

        // Act
        var nextActivities = await signalStart.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("task1", nextActivities[0].NextActivity.ActivityId);
    }
}
