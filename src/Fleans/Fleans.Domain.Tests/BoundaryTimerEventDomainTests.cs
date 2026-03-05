using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class BoundaryTimerEventDomainTests : BoundaryEventDomainTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new BoundaryTimerEvent(boundaryId, attachedToId,
            new TimerDefinition(TimerType.Duration, "PT30M"), IsInterrupting: isInterrupting);

    protected override void AssertEventSpecificProperties(Activity boundary)
    {
        var timer = (BoundaryTimerEvent)boundary;
        Assert.AreEqual("task1", timer.AttachedToActivityId);
        Assert.AreEqual(TimerType.Duration, timer.TimerDefinition.Type);
        Assert.AreEqual("PT30M", timer.TimerDefinition.Expression);
    }
}
