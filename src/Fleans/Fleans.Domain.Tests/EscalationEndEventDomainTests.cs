using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class EscalationEndEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldEmitThrowEscalationAndCompleteWorkflow()
    {
        var escEnd = new EscalationEndEvent("escEnd1", "ESC_001");
        var definition = ActivityTestHelper.CreateWorkflowDefinition([escEnd], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("escEnd1");

        var commands = await escEnd.ExecuteAsync(workflowContext, activityContext, definition);

        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var throwCmd = commands.OfType<ThrowEscalationCommand>().Single();
        Assert.AreEqual("ESC_001", throwCmd.EscalationCode);
        var completeCmd = commands.OfType<CompleteWorkflowCommand>().Single();
        Assert.IsNotNull(completeCmd);
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("escEnd1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty()
    {
        var escEnd = new EscalationEndEvent("escEnd1", "ESC_001");
        var definition = ActivityTestHelper.CreateWorkflowDefinition([escEnd], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("escEnd1");

        var nextActivities = await escEnd.GetNextActivities(workflowContext, activityContext, definition);

        Assert.HasCount(0, nextActivities);
    }
}
