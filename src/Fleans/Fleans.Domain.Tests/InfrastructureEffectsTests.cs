using Fleans.Domain.Effects;
using Fleans.Domain.Events;

namespace Fleans.Domain.Tests;

[TestClass]
public class InfrastructureEffectsTests
{
    [TestMethod]
    public void AllEffects_ShouldImplementIInfrastructureEffect()
    {
        var effects = new IInfrastructureEffect[]
        {
            new RegisterTimerEffect(Guid.NewGuid(), Guid.NewGuid(), "timer1", TimeSpan.FromSeconds(5)),
            new UnregisterTimerEffect(Guid.NewGuid(), Guid.NewGuid(), "timer1"),
            new SubscribeMessageEffect("msg", "key", Guid.NewGuid(), Guid.NewGuid()),
            new UnsubscribeMessageEffect("msg", "key"),
            new SubscribeSignalEffect("sig", Guid.NewGuid(), Guid.NewGuid()),
            new UnsubscribeSignalEffect("sig"),
            new ThrowSignalEffect("sig"),
            new StartChildWorkflowEffect(Guid.NewGuid(), "process-key", new System.Dynamic.ExpandoObject(), "callAct"),
            new NotifyParentCompletedEffect(Guid.NewGuid(), "parentAct", new System.Dynamic.ExpandoObject()),
            new NotifyParentFailedEffect(Guid.NewGuid(), "parentAct", new Exception("err")),
            new PublishDomainEventEffect(new WorkflowCompleted()),
            new CancelActivitySubscriptionsEffect("act1", Guid.NewGuid()),
        };

        Assert.AreEqual(12, effects.Length);
    }
}
