using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

public abstract class BoundaryEventDomainTestBase
{
    protected abstract Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true);

    protected abstract void AssertEventSpecificProperties(Activity boundary);

    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately()
    {
        var boundary = CreateBoundaryEvent("b1", "task1");
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundary, recovery],
            [new SequenceFlow("seq1", boundary, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("b1");

        var commands = await boundary.ExecuteAsync(workflowContext, activityContext, definition);

        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("b1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        var boundary = CreateBoundaryEvent("b1", "task1");
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundary, recovery],
            [new SequenceFlow("seq1", boundary, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("b1");

        var nextActivities = await boundary.GetNextActivities(workflowContext, activityContext, definition);

        Assert.AreEqual(1, nextActivities.Count);
        Assert.AreEqual("recovery", nextActivities[0].NextActivity.ActivityId);
    }

    [TestMethod]
    public void BoundaryEvent_ShouldHaveCorrectProperties()
    {
        var boundary = CreateBoundaryEvent("b1", "task1");
        Assert.AreEqual("b1", boundary.ActivityId);
        AssertEventSpecificProperties(boundary);
    }

    [TestMethod]
    public void BoundaryEvent_IsInterrupting_DefaultsToTrue()
    {
        dynamic boundary = CreateBoundaryEvent("b1", "task1");
        Assert.IsTrue(boundary.IsInterrupting);
    }

    [TestMethod]
    public void BoundaryEvent_IsInterrupting_CanBeSetToFalse()
    {
        dynamic boundary = CreateBoundaryEvent("b1", "task1", isInterrupting: false);
        Assert.IsFalse(boundary.IsInterrupting);
    }
}
