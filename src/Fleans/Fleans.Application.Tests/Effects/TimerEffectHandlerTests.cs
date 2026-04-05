using Fleans.Application.Effects;
using Fleans.Application.Grains;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;

namespace Fleans.Application.Tests.Effects;

[TestClass]
public class TimerEffectHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_RegisterTimer_ActivatesCallbackGrain()
    {
        // Arrange
        var context = Substitute.For<IEffectContext>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var timerGrain = Substitute.For<ITimerCallbackGrain>();

        var workflowInstanceId = Guid.NewGuid();
        var hostActivityInstanceId = Guid.NewGuid();
        var timerActivityId = "timer1";
        var dueTime = TimeSpan.FromSeconds(5);

        grainFactory.GetGrain<ITimerCallbackGrain>(
            workflowInstanceId, $"{hostActivityInstanceId}:{timerActivityId}", null)
            .Returns(timerGrain);
        context.GrainFactory.Returns(grainFactory);

        var handler = new TimerEffectHandler(Substitute.For<ILogger<TimerEffectHandler>>());
        var effect = new RegisterTimerEffect(workflowInstanceId, hostActivityInstanceId, timerActivityId, dueTime);

        // Act
        await handler.HandleAsync(effect, context);

        // Assert
        await timerGrain.Received(1).Activate(dueTime);
    }

    [TestMethod]
    public async Task HandleAsync_UnregisterTimer_CancelsCallbackGrain()
    {
        // Arrange
        var context = Substitute.For<IEffectContext>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var timerGrain = Substitute.For<ITimerCallbackGrain>();

        var workflowInstanceId = Guid.NewGuid();
        var hostActivityInstanceId = Guid.NewGuid();
        var timerActivityId = "timer1";

        grainFactory.GetGrain<ITimerCallbackGrain>(
            workflowInstanceId, $"{hostActivityInstanceId}:{timerActivityId}", null)
            .Returns(timerGrain);
        context.GrainFactory.Returns(grainFactory);

        var handler = new TimerEffectHandler(Substitute.For<ILogger<TimerEffectHandler>>());
        var effect = new UnregisterTimerEffect(workflowInstanceId, hostActivityInstanceId, timerActivityId);

        // Act
        await handler.HandleAsync(effect, context);

        // Assert
        await timerGrain.Received(1).Cancel();
    }

    [TestMethod]
    public void CanHandle_ReturnsTrue_ForTimerEffects()
    {
        var handler = new TimerEffectHandler(Substitute.For<ILogger<TimerEffectHandler>>());

        Assert.IsTrue(handler.CanHandle(new RegisterTimerEffect(Guid.NewGuid(), Guid.NewGuid(), "t", TimeSpan.Zero)));
        Assert.IsTrue(handler.CanHandle(new UnregisterTimerEffect(Guid.NewGuid(), Guid.NewGuid(), "t")));
    }

    [TestMethod]
    public void CanHandle_ReturnsFalse_ForNonTimerEffects()
    {
        var handler = new TimerEffectHandler(Substitute.For<ILogger<TimerEffectHandler>>());

        Assert.IsFalse(handler.CanHandle(new UnsubscribeMessageEffect("msg", "key")));
    }
}
