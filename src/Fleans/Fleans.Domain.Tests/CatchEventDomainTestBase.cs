using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

public abstract class CatchEventDomainTestBase
{
    protected abstract string CatchEventId { get; }
    protected abstract string ExpectedTypeName { get; }

    protected abstract Activity CreateCatchEvent(string activityId);

    protected abstract WorkflowDefinition CreateDefinition(
        List<Activity> activities, List<SequenceFlow> sequenceFlows);

    protected virtual void AssertExecuteCommands(List<IExecutionCommand> commands) { }

    [TestMethod]
    public async Task ExecuteAsync_ShouldCallExecute_AndNotComplete()
    {
        var catchEvent = CreateCatchEvent(CatchEventId);
        var end = new EndEvent("end");
        var definition = CreateDefinition(
            [catchEvent, end],
            [new SequenceFlow("seq1", catchEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext(CatchEventId);

        var commands = await catchEvent.ExecuteAsync(workflowContext, activityContext, definition);

        await activityContext.Received(1).Execute();
        await activityContext.DidNotReceive().Complete();
        AssertExecuteCommands(commands);
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual(CatchEventId, executedEvent.activityId);
        Assert.AreEqual(ExpectedTypeName, executedEvent.TypeName);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        var catchEvent = CreateCatchEvent(CatchEventId);
        var end = new EndEvent("end");
        var definition = CreateDefinition(
            [catchEvent, end],
            [new SequenceFlow("seq1", catchEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext(CatchEventId);

        var nextActivities = await catchEvent.GetNextActivities(workflowContext, activityContext, definition);

        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].NextActivity.ActivityId);
    }
}
