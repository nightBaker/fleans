using Fleans.Application.Effects;
using Fleans.Application.Grains;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orleans;

namespace Fleans.Application.Tests.Effects;

[TestClass]
public class MessageEffectHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_SubscribeMessage_RegistersSubscription()
    {
        // Arrange
        var context = Substitute.For<IEffectContext>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var corrGrain = Substitute.For<IMessageCorrelationGrain>();

        var workflowInstanceId = Guid.NewGuid();
        var hostActivityInstanceId = Guid.NewGuid();
        var activityId = "catchMessage1";
        var messageName = "OrderReceived";
        var correlationKey = "order-123";

        var grainKey = MessageCorrelationKey.Build(messageName, correlationKey);
        grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey, null)
            .Returns(corrGrain);
        context.GrainFactory.Returns(grainFactory);

        var handler = new MessageEffectHandler(Substitute.For<ILogger<MessageEffectHandler>>());
        var effect = new SubscribeMessageEffect(messageName, correlationKey, workflowInstanceId, activityId, hostActivityInstanceId);

        // Act
        await handler.HandleAsync(effect, context);

        // Assert
        await context.Received(1).PersistStateAsync();
        await corrGrain.Received(1).Subscribe(workflowInstanceId, activityId, hostActivityInstanceId);
    }

    [TestMethod]
    public async Task HandleAsync_SubscribeMessageFails_CallsProcessFailureEffects()
    {
        // Arrange
        var context = Substitute.For<IEffectContext>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var corrGrain = Substitute.For<IMessageCorrelationGrain>();

        var workflowInstanceId = Guid.NewGuid();
        var hostActivityInstanceId = Guid.NewGuid();
        var activityId = "catchMessage1";
        var messageName = "OrderReceived";
        var correlationKey = "order-123";

        var grainKey = MessageCorrelationKey.Build(messageName, correlationKey);
        grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey, null)
            .Returns(corrGrain);
        corrGrain.When(g => g.Subscribe(workflowInstanceId, activityId, hostActivityInstanceId))
            .Do(_ => throw new Exception("connection failed"));
        context.GrainFactory.Returns(grainFactory);

        var handler = new MessageEffectHandler(Substitute.For<ILogger<MessageEffectHandler>>());
        var effect = new SubscribeMessageEffect(messageName, correlationKey, workflowInstanceId, activityId, hostActivityInstanceId);

        // Act
        await handler.HandleAsync(effect, context);

        // Assert
        await context.Received(1).ProcessFailureEffects(
            activityId, hostActivityInstanceId, Arg.Is<Exception>(e => e.Message == "connection failed"));
    }

    [TestMethod]
    public async Task HandleAsync_UnsubscribeMessage_CallsUnsubscribe()
    {
        // Arrange
        var context = Substitute.For<IEffectContext>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var corrGrain = Substitute.For<IMessageCorrelationGrain>();

        var messageName = "OrderReceived";
        var correlationKey = "order-123";

        var grainKey = MessageCorrelationKey.Build(messageName, correlationKey);
        grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey, null)
            .Returns(corrGrain);
        context.GrainFactory.Returns(grainFactory);

        var handler = new MessageEffectHandler(Substitute.For<ILogger<MessageEffectHandler>>());
        var effect = new UnsubscribeMessageEffect(messageName, correlationKey);

        // Act
        await handler.HandleAsync(effect, context);

        // Assert
        await corrGrain.Received(1).Unsubscribe();
    }
}
