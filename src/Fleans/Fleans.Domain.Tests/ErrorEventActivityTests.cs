using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class ErrorEventActivityTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallComplete_OnActivityContext()
    {
        // Arrange
        var errorEvent = new ErrorEvent("error1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [errorEvent],
            []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("error1");

        // Act
        await errorEvent.ExecuteAsync(workflowContext, activityContext);

        // Assert
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_WhenSequenceFlowExists()
    {
        // Arrange
        var errorEvent = new ErrorEvent("error1");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [errorEvent, end],
            [new SequenceFlow("seq1", errorEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("error1");

        // Act
        var nextActivities = await errorEvent.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoSequenceFlow()
    {
        // Arrange
        var errorEvent = new ErrorEvent("error1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [errorEvent],
            []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("error1");

        // Act
        var nextActivities = await errorEvent.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(0, nextActivities);
    }
}
