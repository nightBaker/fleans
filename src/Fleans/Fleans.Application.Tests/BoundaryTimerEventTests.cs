using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class BoundaryTimerEventTests : BoundaryEventTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new BoundaryTimerEvent(boundaryId, attachedToId,
            new TimerDefinition(TimerType.Duration, "PT30M"), IsInterrupting: isInterrupting);

    protected override async Task TriggerBoundaryEvent(IWorkflowInstanceGrain instance, Guid hostInstanceId)
        => await instance.HandleTimerFired("boundary1", hostInstanceId);
}
