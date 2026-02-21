using Fleans.Domain;
using Fleans.Domain.Activities;

namespace Fleans.Application.Services;

public interface IBoundaryEventHandler
{
    void Initialize(IBoundaryEventStateAccessor accessor);
    Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId, IWorkflowDefinition definition);
    Task HandleBoundaryMessageFiredAsync(MessageBoundaryEvent boundaryMessage, Guid hostActivityInstanceId, IWorkflowDefinition definition);
    Task HandleBoundaryErrorAsync(string activityId, BoundaryErrorEvent boundaryError, Guid activityInstanceId, IWorkflowDefinition definition);
    Task UnregisterBoundaryTimerRemindersAsync(string activityId, Guid hostActivityInstanceId, IWorkflowDefinition definition);
    Task UnsubscribeBoundaryMessageSubscriptionsAsync(string activityId, Guid variablesId, IWorkflowDefinition definition, string? skipMessageName = null);
    Task HandleBoundarySignalFiredAsync(SignalBoundaryEvent boundarySignal, Guid hostActivityInstanceId, IWorkflowDefinition definition);
    Task UnsubscribeBoundarySignalSubscriptionsAsync(string activityId, IWorkflowDefinition definition, string? skipSignalName = null);
}
