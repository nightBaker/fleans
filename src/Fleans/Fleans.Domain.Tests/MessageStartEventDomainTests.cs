using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class MessageStartEventDomainTests : StartEventDomainTestBase
{
    protected override Activity CreateStartEvent(string activityId)
        => new MessageStartEvent(activityId, "msg_def_1");
}
