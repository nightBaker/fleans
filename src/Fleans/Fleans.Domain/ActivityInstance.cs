
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Fleans.Domain
{
    public partial class ActivityInstance : Grain, IActivityInstance
    {

        private Activity _currentActivity;

        private bool _isCompleted;
        private bool _isExecuting;
        private DateTimeOffset? _createdAt;
        private DateTimeOffset? _executionStartedAt;
        private DateTimeOffset? _completedAt;
        private ActivityErrorState? _errorState;
        private Guid _variablesId;

        private readonly ILogger<ActivityInstance> _logger;

        public ActivityInstance(ILogger<ActivityInstance> logger)
        {
            _logger = logger;
        }

        public ValueTask Complete()
        {
            _isExecuting = false;
            _isCompleted = true;
            _completedAt = DateTimeOffset.UtcNow;
            LogCompleted();
            return ValueTask.CompletedTask;
        }

        public ValueTask Fail(Exception exception)
        {
            if (exception is ActivityException activityException)
            {
                _errorState = activityException.GetActivityErrorState();
            }
            else
            {
                _errorState = new ActivityErrorState(500, exception.Message);
            }

            LogFailed(_errorState.Code, _errorState.Message);

            return Complete();
        }

        public ValueTask Execute()
        {
            _errorState = null;
            _isCompleted = false;
            _isExecuting = true;
            _executionStartedAt = DateTimeOffset.UtcNow;
            RequestContext.Set("VariablesId", _variablesId.ToString());
            LogExecutionStarted();
            return ValueTask.CompletedTask;
        }

        public ValueTask<Guid> GetActivityInstanceId() => ValueTask.FromResult( this.GetPrimaryKey());

        public ValueTask<Activity> GetCurrentActivity() => ValueTask.FromResult(_currentActivity);

        public ValueTask<ActivityErrorState?> GetErrorState() => ValueTask.FromResult(_errorState);

        public ValueTask<DateTimeOffset?> GetCreatedAt() => ValueTask.FromResult(_createdAt);

        public ValueTask<DateTimeOffset?> GetExecutionStartedAt() => ValueTask.FromResult(_executionStartedAt);

        public ValueTask<DateTimeOffset?> GetCompletedAt() => ValueTask.FromResult(_completedAt);

        public ValueTask<ActivityInstanceSnapshot> GetSnapshot() => ValueTask.FromResult(
            new ActivityInstanceSnapshot(
                this.GetPrimaryKey(),
                _currentActivity.ActivityId,
                _currentActivity.GetType().Name,
                _isCompleted,
                _isExecuting,
                _variablesId,
                _errorState,
                _createdAt,
                _executionStartedAt,
                _completedAt));

        ValueTask<bool> IActivityInstance.IsCompleted()
            => ValueTask.FromResult(_isCompleted);

        ValueTask<bool> IActivityInstance.IsExecuting() => ValueTask.FromResult(_isExecuting);

        public ValueTask<Guid> GetVariablesStateId()
            => ValueTask.FromResult(_variablesId);

        public ValueTask SetVariablesId(Guid guid)
        {
            _variablesId = guid;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetActivity(Activity nextActivity)
        {
            _currentActivity = nextActivity;
            _createdAt = DateTimeOffset.UtcNow;
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
