
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Fleans.Domain
{
    public partial class ActivityInstance : Grain, IActivityInstance
    {
        private ActivityInstanceState State { get; } = new();

        private readonly ILogger<ActivityInstance> _logger;

        public ActivityInstance(ILogger<ActivityInstance> logger)
        {
            _logger = logger;
        }

        public ValueTask Complete()
        {
            State.Complete();
            LogCompleted();
            return ValueTask.CompletedTask;
        }

        public ValueTask Fail(Exception exception)
        {
            State.Fail(exception);
            var errorState = State.GetErrorState()!;
            LogFailed(errorState.Code, errorState.Message);
            return Complete();
        }

        public ValueTask Execute()
        {
            State.Execute();
            RequestContext.Set("VariablesId", State.GetVariablesId().ToString());
            LogExecutionStarted();
            return ValueTask.CompletedTask;
        }

        public ValueTask<Guid> GetActivityInstanceId() => ValueTask.FromResult(this.GetPrimaryKey());

        public ValueTask<Activity> GetCurrentActivity() => ValueTask.FromResult(State.GetCurrentActivity()!);

        public ValueTask<ActivityErrorState?> GetErrorState() => ValueTask.FromResult(State.GetErrorState());

        public ValueTask<DateTimeOffset?> GetCreatedAt() => ValueTask.FromResult(State.CreatedAt);

        public ValueTask<DateTimeOffset?> GetExecutionStartedAt() => ValueTask.FromResult(State.ExecutionStartedAt);

        public ValueTask<DateTimeOffset?> GetCompletedAt() => ValueTask.FromResult(State.CompletedAt);

        public ValueTask<ActivityInstanceSnapshot> GetSnapshot() => ValueTask.FromResult(State.GetSnapshot(this.GetPrimaryKey()));

        ValueTask<bool> IActivityInstance.IsCompleted()
            => ValueTask.FromResult(State.IsCompleted());

        ValueTask<bool> IActivityInstance.IsExecuting() => ValueTask.FromResult(State.IsExecuting());

        public ValueTask<Guid> GetVariablesStateId()
            => ValueTask.FromResult(State.GetVariablesId());

        public ValueTask SetVariablesId(Guid guid)
        {
            State.SetVariablesId(guid);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetActivity(Activity nextActivity)
        {
            State.SetActivity(nextActivity);
            return ValueTask.CompletedTask;
        }

        public Task PublishEvent(IDomainEvent domainEvent)
        {
            LogPublishingEvent(domainEvent.GetType().Name);
            var eventPublisher = GrainFactory.GetGrain<IEventPublisher>(0);
            return eventPublisher.Publish(domainEvent);
        }

        [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Activity execution started")]
        private partial void LogExecutionStarted();

        [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Activity completed")]
        private partial void LogCompleted();

        [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Activity failed: {ErrorCode} {ErrorMessage}")]
        private partial void LogFailed(int errorCode, string errorMessage);

        [LoggerMessage(EventId = 2003, Level = LogLevel.Debug, Message = "Publishing event {EventType}")]
        private partial void LogPublishingEvent(string eventType);
    }

    [GenerateSerializer]
    public record ActivityErrorState(int Code, string Message);


}
