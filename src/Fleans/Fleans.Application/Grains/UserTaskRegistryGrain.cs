using Fleans.Domain.States;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Grains;

public partial class UserTaskRegistryGrain : Grain, IUserTaskRegistryGrain
{
    private readonly ILogger<UserTaskRegistryGrain> _logger;
    private readonly Dictionary<Guid, UserTaskRegistration> _tasks = new();

    public UserTaskRegistryGrain(ILogger<UserTaskRegistryGrain> logger)
    {
        _logger = logger;
    }

    public ValueTask Register(UserTaskRegistration registration)
    {
        _tasks[registration.ActivityInstanceId] = registration;
        LogUserTaskRegistered(registration.ActivityInstanceId, registration.ActivityId,
            registration.WorkflowInstanceId);
        return ValueTask.CompletedTask;
    }

    public ValueTask Unregister(Guid activityInstanceId)
    {
        if (_tasks.Remove(activityInstanceId))
        {
            LogUserTaskUnregistered(activityInstanceId);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateClaim(Guid activityInstanceId, string? claimedBy,
        UserTaskLifecycleState taskState)
    {
        if (_tasks.TryGetValue(activityInstanceId, out var existing))
        {
            _tasks[activityInstanceId] = existing with
            {
                ClaimedBy = claimedBy,
                TaskState = taskState
            };
            LogUserTaskClaimUpdated(activityInstanceId, claimedBy, taskState);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<UserTaskRegistration>> GetPendingTasks(
        string? assignee = null, string? candidateGroup = null)
    {
        var query = _tasks.Values
            .Where(t => t.TaskState != UserTaskLifecycleState.Completed);

        if (assignee is not null)
        {
            query = query.Where(t =>
                t.Assignee == assignee ||
                t.CandidateUsers.Contains(assignee));
        }

        if (candidateGroup is not null)
        {
            query = query.Where(t => t.CandidateGroups.Contains(candidateGroup));
        }

        return ValueTask.FromResult<IReadOnlyList<UserTaskRegistration>>(
            query.ToList().AsReadOnly());
    }

    public ValueTask<UserTaskRegistration?> GetTask(Guid activityInstanceId)
    {
        _tasks.TryGetValue(activityInstanceId, out var task);
        return ValueTask.FromResult(task);
    }

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information,
        Message = "User task registered: ActivityInstanceId={ActivityInstanceId}, ActivityId={ActivityId}, WorkflowInstanceId={WorkflowInstanceId}")]
    private partial void LogUserTaskRegistered(Guid activityInstanceId, string activityId, Guid workflowInstanceId);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information,
        Message = "User task unregistered: ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogUserTaskUnregistered(Guid activityInstanceId);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information,
        Message = "User task claim updated: ActivityInstanceId={ActivityInstanceId}, ClaimedBy={ClaimedBy}, TaskState={TaskState}")]
    private partial void LogUserTaskClaimUpdated(Guid activityInstanceId, string? claimedBy, UserTaskLifecycleState taskState);
}
