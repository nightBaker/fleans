using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class MessageBoundaryEventDomainTests : BoundaryEventDomainTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new MessageBoundaryEvent(boundaryId, attachedToId, "msg_payment", IsInterrupting: isInterrupting);

    protected override void AssertEventSpecificProperties(Activity boundary)
    {
        var msg = (MessageBoundaryEvent)boundary;
        Assert.AreEqual("task1", msg.AttachedToActivityId);
        Assert.AreEqual("msg_payment", msg.MessageDefinitionId);
    }
}
