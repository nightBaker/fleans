using Fleans.Domain.Activities;
using Fleans.Domain.Errors;

namespace Fleans.Domain.States;

public class ActivityInstanceState
{
    private Activity? _currentActivity;
    private bool _isExecuting;
    private bool _isCompleted;
    private Guid _variablesId;
    private ActivityErrorState? _errorState;
    private DateTimeOffset? _createdAt;
    private DateTimeOffset? _executionStartedAt;
    private DateTimeOffset? _completedAt;

    public Activity? GetCurrentActivity() => _currentActivity;
    public bool IsCompleted() => _isCompleted;
    public bool IsExecuting() => _isExecuting;
    public Guid GetVariablesId() => _variablesId;
    public ActivityErrorState? GetErrorState() => _errorState;
    public DateTimeOffset? CreatedAt => _createdAt;
    public DateTimeOffset? ExecutionStartedAt => _executionStartedAt;
    public DateTimeOffset? CompletedAt => _completedAt;

    public void Complete()
    {
        _isExecuting = false;
        _isCompleted = true;
        _completedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(Exception exception)
    {
        if (exception is ActivityException activityException)
            _errorState = activityException.GetActivityErrorState();
        else
            _errorState = new ActivityErrorState(500, exception.Message);
    }

    public void Execute()
    {
        _errorState = null;
        _isCompleted = false;
        _isExecuting = true;
        _executionStartedAt = DateTimeOffset.UtcNow;
    }

    public void SetActivity(Activity activity)
    {
        _currentActivity = activity;
        _createdAt = DateTimeOffset.UtcNow;
    }

    public void SetVariablesId(Guid id) => _variablesId = id;

    public ActivityInstanceSnapshot GetSnapshot(Guid grainId) =>
        new(grainId, _currentActivity!.ActivityId, _currentActivity.GetType().Name,
            _isCompleted, _isExecuting, _variablesId, _errorState,
            _createdAt, _executionStartedAt, _completedAt);
}
