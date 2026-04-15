using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class UserTaskGrain : Grain, IUserTaskGrain
{
    private readonly ILogger<UserTaskGrain> _logger;
    private readonly IPersistentState<UserTaskState> _state;

    public UserTaskGrain(
        ILogger<UserTaskGrain> logger,
        [PersistentState("state", GrainStorageNames.UserTasks)]
        IPersistentState<UserTaskState> state)
    {
        _logger = logger;
        _state = state;
    }

    public async Task Register(Guid workflowInstanceId, string activityId,
        string? assignee, IReadOnlyList<string> candidateGroups,
        IReadOnlyList<string> candidateUsers, IReadOnlyList<string>? expectedOutputVariables)
    {
        var activityInstanceId = this.GetPrimaryKey();
        _state.State.ActivityInstanceId = activityInstanceId;
        _state.State.WorkflowInstanceId = workflowInstanceId;
        _state.State.ActivityId = activityId;
        _state.State.Assignee = assignee;
        _state.State.CandidateGroups = candidateGroups;
        _state.State.CandidateUsers = candidateUsers;
        _state.State.ExpectedOutputVariables = expectedOutputVariables;
        _state.State.TaskState = UserTaskLifecycleState.Created;
        _state.State.CreatedAt = DateTimeOffset.UtcNow;

        await _state.WriteStateAsync();
        LogUserTaskRegistered(activityInstanceId, activityId, workflowInstanceId);
    }

    public async Task UpdateClaim(string? claimedBy, UserTaskLifecycleState taskState)
    {
        var activityInstanceId = this.GetPrimaryKey();
        _state.State.ClaimedBy = claimedBy;
        _state.State.ClaimedAt = claimedBy is not null ? DateTimeOffset.UtcNow : null;
        _state.State.TaskState = taskState;

        await _state.WriteStateAsync();
        LogUserTaskClaimUpdated(activityInstanceId, claimedBy, taskState);
    }

    public async Task MarkCompleted()
    {
        var activityInstanceId = this.GetPrimaryKey();
        await _state.ClearStateAsync();
        LogUserTaskCompleted(activityInstanceId);
    }

    public Task<UserTaskState?> GetState()
    {
        if (!_state.RecordExists)
            return Task.FromResult<UserTaskState?>(null);

        return Task.FromResult<UserTaskState?>(_state.State);
    }

    [LoggerMessage(EventId = 4100, Level = LogLevel.Information,
        Message = "User task registered: ActivityInstanceId={ActivityInstanceId}, ActivityId={ActivityId}, WorkflowInstanceId={WorkflowInstanceId}")]
    private partial void LogUserTaskRegistered(Guid activityInstanceId, string activityId, Guid workflowInstanceId);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information,
        Message = "User task claim updated: ActivityInstanceId={ActivityInstanceId}, ClaimedBy={ClaimedBy}, TaskState={TaskState}")]
    private partial void LogUserTaskClaimUpdated(Guid activityInstanceId, string? claimedBy, UserTaskLifecycleState taskState);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Information,
        Message = "User task completed: ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogUserTaskCompleted(Guid activityInstanceId);
}
