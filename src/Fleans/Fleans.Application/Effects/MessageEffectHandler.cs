using Fleans.Application.Grains;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Effects;

public partial class MessageEffectHandler : IEffectHandler
{
    private readonly ILogger<MessageEffectHandler> _logger;

    public MessageEffectHandler(ILogger<MessageEffectHandler> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(IInfrastructureEffect effect) =>
        effect is SubscribeMessageEffect or UnsubscribeMessageEffect;

    public async Task HandleAsync(IInfrastructureEffect effect, IEffectContext context)
    {
        switch (effect)
        {
            case SubscribeMessageEffect subMsg:
                await PerformMessageSubscribe(subMsg, context);
                break;

            case UnsubscribeMessageEffect unsubMsg:
                var unsubMsgKey = MessageCorrelationKey.Build(unsubMsg.MessageName, unsubMsg.CorrelationKey);
                var unsubMsgGrain = context.GrainFactory.GetGrain<IMessageCorrelationGrain>(unsubMsgKey);
                await unsubMsgGrain.Unsubscribe();
                break;

            default:
                throw new InvalidOperationException($"Unexpected effect type in {nameof(MessageEffectHandler)}: {effect.GetType().Name}");
        }
    }

    private async Task PerformMessageSubscribe(SubscribeMessageEffect subMsg, IEffectContext context)
    {
        var grainKey = MessageCorrelationKey.Build(subMsg.MessageName, subMsg.CorrelationKey);
        var corrGrain = context.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);

        try
        {
            await context.PersistStateAsync(); // persist before external call
            await corrGrain.Subscribe(subMsg.WorkflowInstanceId, subMsg.ActivityId, subMsg.HostActivityInstanceId);
            LogMessageSubscriptionRegistered(subMsg.ActivityId, subMsg.MessageName, subMsg.CorrelationKey);
        }
        catch (Exception ex)
        {
            LogMessageSubscriptionFailed(subMsg.ActivityId, subMsg.MessageName, subMsg.CorrelationKey, ex);
            await context.ProcessFailureEffects(subMsg.ActivityId, subMsg.HostActivityInstanceId, ex);
        }
    }

    [LoggerMessage(EventId = 1021, Level = LogLevel.Information,
        Message = "Message subscription registered for activity {ActivityId}: messageName={MessageName}, correlationKey={CorrelationKey}")]
    private partial void LogMessageSubscriptionRegistered(string activityId, string messageName, string correlationKey);

    [LoggerMessage(EventId = 1023, Level = LogLevel.Warning,
        Message = "Message subscription failed for activity {ActivityId}: messageName={MessageName}, correlationKey={CorrelationKey}")]
    private partial void LogMessageSubscriptionFailed(string activityId, string messageName, string correlationKey, Exception exception);
}
