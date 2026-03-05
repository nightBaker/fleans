using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Tests;

[TestClass]
public class TimerIntermediateCatchEventDomainTests : CatchEventDomainTestBase
{
    protected override string CatchEventId => "timer1";
    protected override string ExpectedTypeName => "TimerIntermediateCatchEvent";

    protected override Activity CreateCatchEvent(string activityId)
        => new TimerIntermediateCatchEvent(activityId, new TimerDefinition(TimerType.Duration, "PT5M"));

    protected override WorkflowDefinition CreateDefinition(
        List<Activity> activities, List<SequenceFlow> sequenceFlows)
        => ActivityTestHelper.CreateWorkflowDefinition(activities, sequenceFlows);
}
