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
        Assert.IsTrue(commands.OfType<CompleteCommand>().Any());
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
        Assert.AreEqual("recovery", nextActivities[0].ActivityId);
    }
}
