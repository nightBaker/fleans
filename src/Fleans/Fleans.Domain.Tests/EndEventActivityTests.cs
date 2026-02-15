using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class EndEventActivityTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallComplete_OnActivityAndWorkflowContexts()
    {
        // Arrange
        var endEvent = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [endEvent],
            []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("end");

        // Act
        await endEvent.ExecuteAsync(workflowContext, activityContext);

        // Assert
        await activityContext.Received(1).Complete();
        await workflowContext.Received(1).Complete();
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldAlwaysReturnEmptyList()
    {
        // Arrange
        var endEvent = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [endEvent],
            []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("end");

        // Act
        var nextActivities = await endEvent.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(0, nextActivities);
    }
}
