using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Tests;

[TestClass]
public class SignalIntermediateCatchEventDomainTests : CatchEventDomainTestBase
{
    protected override string CatchEventId => "sigCatch1";
    protected override string ExpectedTypeName => "SignalIntermediateCatchEvent";

    protected override Activity CreateCatchEvent(string activityId)
        => new SignalIntermediateCatchEvent(activityId, "sig1");

    protected override WorkflowDefinition CreateDefinition(
        List<Activity> activities, List<SequenceFlow> sequenceFlows)
        => ActivityTestHelper.CreateDefinitionWithSignal(activities, sequenceFlows);

    protected override void AssertExecuteCommands(List<IExecutionCommand> commands)
    {
        var sigCmd = commands.OfType<RegisterSignalCommand>().Single();
        Assert.AreEqual("order_shipped", sigCmd.SignalName);
        Assert.AreEqual(CatchEventId, sigCmd.ActivityId);
        Assert.IsFalse(sigCmd.IsBoundary);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoOutgoingFlow()
    {
        var sigCatch = CreateCatchEvent(CatchEventId);
        var definition = CreateDefinition([sigCatch], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext(CatchEventId);

        var nextActivities = await sigCatch.GetNextActivities(workflowContext, activityContext, definition);

        Assert.HasCount(0, nextActivities);
    }
}
