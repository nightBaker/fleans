using Fleans.Application.Effects;
using Fleans.Application.Grains;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;

namespace Fleans.Application.Tests.Effects;

[TestClass]
public class SignalEffectHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_SubscribeSignal_RegistersSubscription()
    {
        var context = Substitute.For<IEffectContext>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var signalGrain = Substitute.For<ISignalCorrelationGrain>();

        var workflowInstanceId = Guid.NewGuid();
        var hostActivityInstanceId = Guid.NewGuid();
        var activityId = "catchSignal1";
        var signalName = "GlobalAlert";

        grainFactory.GetGrain<ISignalCorrelationGrain>(signalName, null)
            .Returns(signalGrain);
        context.GrainFactory.Returns(grainFactory);

        var handler = new SignalEffectHandler(Substitute.For<ILogger<SignalEffectHandler>>());
        var effect = new SubscribeSignalEffect(signalName, workflowInstanceId, activityId, hostActivityInstanceId);

        await handler.HandleAsync(effect, context);

        await context.Received(1).PersistStateAsync();
        await signalGrain.Received(1).Subscribe(workflowInstanceId, activityId, hostActivityInstanceId);
    }

    [TestMethod]
    public async Task HandleAsync_SubscribeSignalFails_CallsProcessFailureEffects()
    {
        // #425: registration-path failures must surface as workflow failures.
        var context = Substitute.For<IEffectContext>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var signalGrain = Substitute.For<ISignalCorrelationGrain>();

        var workflowInstanceId = Guid.NewGuid();
        var hostActivityInstanceId = Guid.NewGuid();
        var activityId = "catchSignal1";
        var signalName = "GlobalAlert";

        grainFactory.GetGrain<ISignalCorrelationGrain>(signalName, null)
            .Returns(signalGrain);
        signalGrain.When(g => g.Subscribe(workflowInstanceId, activityId, hostActivityInstanceId))
            .Do(_ => throw new Exception("connection failed"));
        context.GrainFactory.Returns(grainFactory);

        var handler = new SignalEffectHandler(Substitute.For<ILogger<SignalEffectHandler>>());
        var effect = new SubscribeSignalEffect(signalName, workflowInstanceId, activityId, hostActivityInstanceId);

        await handler.HandleAsync(effect, context);

        await context.Received(1).ProcessFailureEffects(
            activityId, hostActivityInstanceId, Arg.Is<Exception>(e => e.Message == "connection failed"));
    }

    [TestMethod]
    public async Task HandleAsync_UnsubscribeSignal_CallsUnsubscribe()
    {
        var context = Substitute.For<IEffectContext>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var signalGrain = Substitute.For<ISignalCorrelationGrain>();

        var workflowInstanceId = Guid.NewGuid();
        var activityId = "catchSignal1";
        var signalName = "GlobalAlert";

        grainFactory.GetGrain<ISignalCorrelationGrain>(signalName, null)
            .Returns(signalGrain);
        context.GrainFactory.Returns(grainFactory);

        var handler = new SignalEffectHandler(Substitute.For<ILogger<SignalEffectHandler>>());
        var effect = new UnsubscribeSignalEffect(signalName, workflowInstanceId, activityId);

        await handler.HandleAsync(effect, context);

        await signalGrain.Received(1).Unsubscribe(workflowInstanceId, activityId);
    }
}
