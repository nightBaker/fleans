using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class CallActivityDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallStartChildWorkflow()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "childProcess",
            [new VariableMapping("orderId", "orderId")],
            [new VariableMapping("result", "result")],
            PropagateAllParentVariables: false,
            PropagateAllChildVariables: false);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [callActivity, end],
            [new SequenceFlow("seq1", callActivity, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("call1");

        // Act
        await callActivity.ExecuteAsync(workflowContext, activityContext, Guid.NewGuid());

        // Assert
        await activityContext.Received(1).Execute();
        await workflowContext.Received(1).StartChildWorkflow(callActivity, activityContext);
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("call1", executedEvent.activityId);
        Assert.AreEqual("CallActivity", executedEvent.TypeName);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnSingleTarget_ViaSequenceFlow()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "childProcess", [], []);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [callActivity, end],
            [new SequenceFlow("seq1", callActivity, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("call1");

        // Act
        var nextActivities = await callActivity.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoOutgoingFlow()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "childProcess", [], []);
        var definition = ActivityTestHelper.CreateWorkflowDefinition([callActivity], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("call1");

        // Act
        var nextActivities = await callActivity.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(0, nextActivities);
    }
}
