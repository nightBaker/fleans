using Fleans.Domain.Activities;

namespace Fleans.Application.Services;

public interface IBoundaryEventHandler
{
    void Initialize(IBoundaryEventStateAccessor accessor);
    Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId);
    Task HandleBoundaryMessageFiredAsync(MessageBoundaryEvent boundaryMessage, Guid hostActivityInstanceId);
    Task HandleBoundaryErrorAsync(string activityId, BoundaryErrorEvent boundaryError, Guid activityInstanceId);
    Task UnregisterBoundaryTimerRemindersAsync(string activityId, Guid hostActivityInstanceId);
    Task UnsubscribeBoundaryMessageSubscriptionsAsync(string activityId, string? skipMessageName = null);
}
