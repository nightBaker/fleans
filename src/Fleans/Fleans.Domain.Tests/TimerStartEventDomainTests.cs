using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class TimerStartEventDomainTests : StartEventDomainTestBase
{
    protected override Activity CreateStartEvent(string activityId)
        => new TimerStartEvent(activityId, new TimerDefinition(TimerType.Cycle, "R3/PT10M"));
}
