using Fleans.Application.ExternalTasks;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Effects;

/// <summary>
/// Tells the <see cref="ExternalTaskDispatcherGrain"/> running a cancelled custom task to stop
/// waiting and cancel it on its worker, so the handler's <c>CancellationToken</c> fires (#786).
/// </summary>
public partial class CustomTaskEffectHandler : IEffectHandler
{
    private readonly ILogger<CustomTaskEffectHandler> _logger;

    public CustomTaskEffectHandler(ILogger<CustomTaskEffectHandler> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(IInfrastructureEffect effect) => effect is CancelCustomTaskEffect;

    public async Task HandleAsync(IInfrastructureEffect effect, IEffectContext context)
    {
        if (effect is not CancelCustomTaskEffect cancel)
            throw new InvalidOperationException($"Unexpected effect type in {nameof(CustomTaskEffectHandler)}: {effect.GetType().Name}");

        // Persist first: the dispatch acks its event once cancelled, so a cancellation that never
        // reached the journal (failed append, crash) would leave the task active with nothing
        // left to deliver it.
        await context.PersistStateAsync();

        // Cleanup: the activity is already cancelled, so a failure here is logged only — the
        // dispatch then notices at its next claim renewal. One-way, so a dispatch that is calling
        // into this instance cannot deadlock it.
        try
        {
            LogCancellingCustomTask(cancel.ActivityInstanceId, cancel.DispatchId);
            await context.GrainFactory
                .GetGrain<IExternalTaskDispatcherGrain>(cancel.ActivityInstanceId.ToString("D"))
                .Cancel(cancel.DispatchId);
        }
        catch (Exception ex)
        {
            LogCancelCustomTaskFailed(cancel.ActivityInstanceId, ex.Message);
        }
    }

    [LoggerMessage(EventId = 1260, Level = LogLevel.Information,
        Message = "Cancelling custom task {ActivityInstanceId} (dispatch {DispatchId}) on its worker: the activity was cancelled")]
    private partial void LogCancellingCustomTask(Guid activityInstanceId, Guid dispatchId);

    [LoggerMessage(EventId = 1261, Level = LogLevel.Warning,
        Message = "Cancelling custom task {ActivityInstanceId} on its worker failed; its dispatch stops at the next claim renewal: {Reason}")]
    private partial void LogCancelCustomTaskFailed(Guid activityInstanceId, string reason);
}
