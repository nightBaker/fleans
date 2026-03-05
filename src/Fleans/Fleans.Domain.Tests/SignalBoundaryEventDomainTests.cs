using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class SignalBoundaryEventDomainTests : BoundaryEventDomainTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new SignalBoundaryEvent(boundaryId, attachedToId, "sig_order", IsInterrupting: isInterrupting);

    protected override void AssertEventSpecificProperties(Activity boundary)
    {
        var sig = (SignalBoundaryEvent)boundary;
        Assert.AreEqual("task1", sig.AttachedToActivityId);
        Assert.AreEqual("sig_order", sig.SignalDefinitionId);
    }
}
