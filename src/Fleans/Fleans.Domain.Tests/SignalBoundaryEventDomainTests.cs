using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class SignalBoundaryEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately()
    {
        // Arrange
        var boundary = new SignalBoundaryEvent("bsig1", "task1", "sig_order");
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundary, recovery],
            [new SequenceFlow("seq1", boundary, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("bsig1");

        // Act
        var commands = await boundary.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — boundary executes and completes immediately (signal already delivered)
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("bsig1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var boundary = new SignalBoundaryEvent("bsig1", "task1", "sig_order");
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundary, recovery],
            [new SequenceFlow("seq1", boundary, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("bsig1");

        // Act
        var nextActivities = await boundary.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("recovery", nextActivities[0].NextActivity.ActivityId);
    }

    [TestMethod]
    public void SignalBoundaryEvent_ShouldHaveCorrectProperties()
    {
        var boundary = new SignalBoundaryEvent("bsig1", "task1", "sig_order");

        Assert.AreEqual("bsig1", boundary.ActivityId);
        Assert.AreEqual("task1", boundary.AttachedToActivityId);
        Assert.AreEqual("sig_order", boundary.SignalDefinitionId);
    }
}
