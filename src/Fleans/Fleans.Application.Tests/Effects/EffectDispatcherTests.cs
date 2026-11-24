using Fleans.Application.Effects;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Fleans.Application.Tests.Effects;

[TestClass]
public class EffectDispatcherTests
{
    private static (EffectDispatcher dispatcher, IEffectHandler handler) Create()
    {
        var handler = Substitute.For<IEffectHandler>();
        handler.CanHandle(Arg.Any<IInfrastructureEffect>()).Returns(true);
        return (new EffectDispatcher([handler], Substitute.For<ILogger<EffectDispatcher>>()), handler);
    }

    [TestMethod]
    public async Task DispatchAsync_SkipsUnregisterOfTheFiredTimer_OnlyThatOne()
    {
        // The fired timer's TimerCallbackGrain is the non-reentrant caller of this turn;
        // unregistering it would deadlock until the Orleans response timeout.
        var (dispatcher, handler) = Create();
        var instanceId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var context = Substitute.For<IEffectContext>();
        context.FiredTimer.Returns(("timer1", hostId));

        var fired = new UnregisterTimerEffect(instanceId, hostId, "timer1");
        var sameIdOtherHost = new UnregisterTimerEffect(instanceId, Guid.NewGuid(), "timer1");
        var otherTimer = new UnregisterTimerEffect(instanceId, hostId, "timer2");

        await dispatcher.DispatchAsync([fired, sameIdOtherHost, otherTimer], context);

        await handler.DidNotReceive().HandleAsync(fired, context);
        await handler.Received(1).HandleAsync(sameIdOtherHost, context);
        await handler.Received(1).HandleAsync(otherTimer, context);
    }

    [TestMethod]
    public async Task DispatchAsync_TerminalInstance_SkipsListenerRegistrationsButNotCleanups()
    {
        var (dispatcher, handler) = Create();
        var instanceId = Guid.NewGuid();
        var context = Substitute.For<IEffectContext>();
        context.IsInstanceTerminal.Returns(true);

        IInfrastructureEffect[] registrations =
        [
            new RegisterTimerEffect(instanceId, instanceId, "t", TimeSpan.FromSeconds(1)),
            new SubscribeMessageEffect("m", "42", instanceId, "a", instanceId),
            new SubscribeSignalEffect("s", instanceId, "a", instanceId),
        ];
        IInfrastructureEffect[] cleanups =
        [
            new UnregisterTimerEffect(instanceId, instanceId, "t"),
            new UnsubscribeMessageEffect("m", "42"),
            new UnsubscribeSignalEffect("s", instanceId, "a"),
        ];

        await dispatcher.DispatchAsync([.. registrations, .. cleanups], context);

        foreach (var effect in registrations)
            await handler.DidNotReceive().HandleAsync(effect, context);
        foreach (var effect in cleanups)
            await handler.Received(1).HandleAsync(effect, context);
    }
}
