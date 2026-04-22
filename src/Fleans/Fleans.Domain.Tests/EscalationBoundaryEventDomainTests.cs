using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class EscalationBoundaryEventDomainTests : BoundaryEventDomainTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new EscalationBoundaryEvent(boundaryId, attachedToId, "ESC_001", IsInterrupting: isInterrupting);

    protected override void AssertEventSpecificProperties(Activity boundary)
    {
        var esc = (EscalationBoundaryEvent)boundary;
        Assert.AreEqual("task1", esc.AttachedToActivityId);
        Assert.AreEqual("ESC_001", esc.EscalationCode);
    }

    [TestMethod]
    public void EscalationBoundaryEvent_CatchAll_HasNullEscalationCode()
    {
        var boundary = new EscalationBoundaryEvent("b1", "task1", null);
        Assert.IsNull(boundary.EscalationCode);
    }
}
