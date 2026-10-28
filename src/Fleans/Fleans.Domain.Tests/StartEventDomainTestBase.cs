using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

// Typed start events (message, signal, timer) execute like a plain StartEvent:
// complete immediately and follow their single outgoing flow.
public abstract class StartEventDomainTestBase
{
    protected abstract Activity CreateStartEvent(string activityId);

    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately_LikeStartEvent()
    {
        var startEvent = CreateStartEvent("start1");
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [startEvent, task],
            [new SequenceFlow("seq1", startEvent, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("start1");

        await startEvent.ExecuteAsync(workflowContext, activityContext, definition);

        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("start1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        var startEvent = CreateStartEvent("start1");
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [startEvent, task],
            [new SequenceFlow("seq1", startEvent, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("start1");

        var nextActivities = await startEvent.GetNextActivities(workflowContext, activityContext, definition);

        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("task1", nextActivities[0].NextActivity.ActivityId);
    }
}
