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
        var commands = await endEvent.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Complete();
        Assert.IsTrue(commands.OfType<CompleteWorkflowCommand>().Any());
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
        var nextActivities = await endEvent.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(0, nextActivities);
    }
}
