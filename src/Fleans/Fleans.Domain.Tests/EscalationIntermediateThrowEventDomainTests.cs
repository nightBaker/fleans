using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class EscalationIntermediateThrowEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldEmitThrowEscalationCommand_AndComplete()
    {
        var escThrow = new EscalationIntermediateThrowEvent("escThrow1", "ESC_001");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [escThrow, end],
            [new SequenceFlow("seq1", escThrow, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("escThrow1");

        var commands = await escThrow.ExecuteAsync(workflowContext, activityContext, definition);

        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var throwCmd = commands.OfType<ThrowEscalationCommand>().Single();
        Assert.AreEqual("ESC_001", throwCmd.EscalationCode);
        // Unlike EscalationEndEvent, intermediate throw does NOT emit CompleteWorkflowCommand
        Assert.IsFalse(commands.OfType<CompleteWorkflowCommand>().Any());
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("escThrow1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        var escThrow = new EscalationIntermediateThrowEvent("escThrow1", "ESC_001");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [escThrow, end],
            [new SequenceFlow("seq1", escThrow, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("escThrow1");

        var nextActivities = await escThrow.GetNextActivities(workflowContext, activityContext, definition);

        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].NextActivity.ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoOutgoingFlow()
    {
        var escThrow = new EscalationIntermediateThrowEvent("escThrow1", "ESC_001");
        var definition = ActivityTestHelper.CreateWorkflowDefinition([escThrow], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("escThrow1");

        var nextActivities = await escThrow.GetNextActivities(workflowContext, activityContext, definition);

        Assert.HasCount(0, nextActivities);
    }
}
