using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Tests;

[TestClass]
public class MessageIntermediateCatchEventDomainTests : CatchEventDomainTestBase
{
    protected override string CatchEventId => "msgCatch1";
    protected override string ExpectedTypeName => "MessageIntermediateCatchEvent";

    protected override Activity CreateCatchEvent(string activityId)
        => new MessageIntermediateCatchEvent(activityId, "msg_payment");

    protected override WorkflowDefinition CreateDefinition(
        List<Activity> activities, List<SequenceFlow> sequenceFlows)
        => ActivityTestHelper.CreateWorkflowDefinition(activities, sequenceFlows);

    protected override void AssertExecuteCommands(List<IExecutionCommand> commands)
    {
        var msgCmd = commands.OfType<RegisterMessageCommand>().Single();
        Assert.AreEqual("msg_payment", msgCmd.MessageDefinitionId);
        Assert.AreEqual(CatchEventId, msgCmd.ActivityId);
        Assert.IsFalse(msgCmd.IsBoundary);
    }
}
