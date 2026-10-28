using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class SignalStartEventDomainTests : StartEventDomainTestBase
{
    protected override Activity CreateStartEvent(string activityId)
        => new SignalStartEvent(activityId, "sig_def_1");
}
