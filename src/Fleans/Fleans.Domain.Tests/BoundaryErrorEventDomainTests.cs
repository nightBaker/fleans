using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class BoundaryErrorEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately()
    {
        // Arrange
        var boundaryEvent = new BoundaryErrorEvent("err1", "call1", null);
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundaryEvent, recovery],
            [new SequenceFlow("seq1", boundaryEvent, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("err1");

        // Act
        var commands = await boundaryEvent.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("err1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var boundaryEvent = new BoundaryErrorEvent("err1", "call1", null);
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundaryEvent, recovery],
            [new SequenceFlow("seq1", boundaryEvent, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("err1");

        // Act
        var nextActivities = await boundaryEvent.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("recovery", nextActivities[0].NextActivity.ActivityId);
    }

    [TestMethod]
    public void BoundaryErrorEvent_IsInterrupting_DefaultsToTrue()
    {
        var boundary = new BoundaryErrorEvent("err1", "call1", null);
        Assert.IsTrue(boundary.IsInterrupting);
    }

    [TestMethod]
    public void BoundaryErrorEvent_IsInterrupting_CanBeSetToFalse()
    {
        var boundary = new BoundaryErrorEvent("err1", "call1", null, IsInterrupting: false);
        Assert.IsFalse(boundary.IsInterrupting);
    }
}
