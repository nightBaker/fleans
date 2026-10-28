using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class BoundaryErrorEventDomainTests : BoundaryEventDomainTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new BoundaryErrorEvent(boundaryId, attachedToId, "PaymentFailed", IsInterrupting: isInterrupting);

    protected override void AssertEventSpecificProperties(Activity boundary)
    {
        var error = (BoundaryErrorEvent)boundary;
        Assert.AreEqual("task1", error.AttachedToActivityId);
        Assert.AreEqual("PaymentFailed", error.ErrorCode);
    }
}
