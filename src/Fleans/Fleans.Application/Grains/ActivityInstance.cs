using Fleans.Domain;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class ActivityInstance : Grain, IActivityInstanceGrain
{
    private readonly IPersistentState<ActivityInstanceState> _state;
    private readonly ILogger<ActivityInstance> _logger;

    private ActivityInstanceState State => _state.State;

    public ActivityInstance(
        [PersistentState("state", GrainStorageNames.ActivityInstances)] IPersistentState<ActivityInstanceState> state,
        ILogger<ActivityInstance> logger)
    {
        _state = state;
        _logger = logger;
    }

    public async ValueTask Complete()
    {
        State.Complete();
        LogCompleted();
        await _state.WriteStateAsync();
    }

    public async ValueTask Fail(Exception exception)
    {
        State.Fail(exception);
        var errorState = State.ErrorState!;
        LogFailed(errorState.Code, errorState.Message);
        await _state.WriteStateAsync();
    }

    public async ValueTask Cancel(string reason)
    {
        State.Cancel(reason);
        LogCancelled(reason);
        await _state.WriteStateAsync();
    }

    public async ValueTask Execute()
    {
        State.Execute();
        RequestContext.Set("VariablesId", State.VariablesId.ToString());
        LogExecutionStarted();
        await _state.WriteStateAsync();
    }

    public ValueTask<Guid> GetActivityInstanceId() => ValueTask.FromResult(this.GetPrimaryKey());

    public ValueTask<string> GetActivityId() => ValueTask.FromResult(State.ActivityId!);

    public ValueTask<bool> IsCompleted()
        => ValueTask.FromResult(State.IsCompleted);

    public ValueTask<bool> IsExecuting() => ValueTask.FromResult(State.IsExecuting);

    public ValueTask<bool> IsCancelled() => ValueTask.FromResult(State.IsCancelled);

    public ValueTask<Guid> GetVariablesStateId()
        => ValueTask.FromResult(State.VariablesId);

    public ValueTask<ActivityErrorState?> GetErrorState()
        => ValueTask.FromResult(State.ErrorState);

    public async ValueTask SetVariablesId(Guid guid)
    {
        State.SetVariablesId(guid);
        await _state.WriteStateAsync();
    }

    public async ValueTask SetActivity(string activityId, string activityType)
    {
        State.SetActivity(activityId, activityType);
        await _state.WriteStateAsync();
    }

    public ValueTask PublishEvent(IDomainEvent domainEvent)
    {
        LogPublishingEvent(domainEvent.GetType().Name);
        var eventPublisher = GrainFactory.GetGrain<IEventPublisher>(0);
        return new ValueTask(eventPublisher.Publish(domainEvent));
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Activity execution started")]
    private partial void LogExecutionStarted();

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Activity completed")]
    private partial void LogCompleted();

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Activity failed: {ErrorCode} {ErrorMessage}")]
    private partial void LogFailed(int errorCode, string errorMessage);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Debug, Message = "Publishing event {EventType}")]
    private partial void LogPublishingEvent(string eventType);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "Activity cancelled: {Reason}")]
    private partial void LogCancelled(string reason);
}
