using System.Dynamic;
using Fleans.Domain.Effects;

using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Domain.Aggregates.Services;

public class UserTaskLifecycle
{
    private readonly WorkflowInstanceState _state;
    private readonly Action<IDomainEvent> _emit;

    public UserTaskLifecycle(
        WorkflowInstanceState state,
        Action<IDomainEvent> emit)
    {
        _state = state;
        _emit = emit;
    }

    public IReadOnlyList<IInfrastructureEffect> Claim(
        Guid activityInstanceId, string userId)
    {
        _state.GetActiveEntry(activityInstanceId);
        var metadata = _state.UserTasks.GetValueOrDefault(activityInstanceId)
            ?? throw new InvalidOperationException(
                $"Activity instance '{activityInstanceId}' is not a user task.");

        // Authorization: user must match assignee OR be in candidate users list.
        // When both are set, satisfying either condition is sufficient.
        var matchesAssignee = metadata.Assignee is null || metadata.Assignee == userId;
        var matchesCandidateUsers = metadata.CandidateUsers.Count == 0
            || metadata.CandidateUsers.Contains(userId);

        if (metadata.Assignee is not null && metadata.CandidateUsers.Count > 0)
        {
            // Both constraints set — OR logic
            if (!matchesAssignee && !matchesCandidateUsers)
                throw new InvalidOperationException(
                    $"User {userId} is neither the assignee ({metadata.Assignee}) nor in the candidate users list");
        }
        else
        {
            // Only one constraint set — must satisfy it
            if (!matchesAssignee)
                throw new InvalidOperationException(
                    $"Task is assigned to {metadata.Assignee}, not {userId}");
            if (!matchesCandidateUsers)
                throw new InvalidOperationException(
                    $"User {userId} is not in candidate users list");
        }

        var claimedAt = DateTimeOffset.UtcNow;
        _emit(new UserTaskClaimed(activityInstanceId, userId, claimedAt));

        return [new UpdateUserTaskClaimEffect(
            activityInstanceId, userId, UserTaskLifecycleState.Claimed)];
    }

    public IReadOnlyList<IInfrastructureEffect> Unclaim(Guid activityInstanceId)
    {
        // Validate entry is still active (not completed/cancelled by boundary event)
        _state.GetActiveEntry(activityInstanceId);

        _ = _state.UserTasks.GetValueOrDefault(activityInstanceId)
            ?? throw new InvalidOperationException(
                $"Activity instance '{activityInstanceId}' is not a user task.");

        _emit(new UserTaskUnclaimed(activityInstanceId));

        return [new UpdateUserTaskClaimEffect(
            activityInstanceId, null, UserTaskLifecycleState.Created)];
    }

    /// <summary>
    /// Validates user task completion preconditions (claimed by this user, expected outputs present).
    /// Returns the resolved entry for the caller to pass to CompleteActivityInternal.
    /// </summary>
    public ActivityInstanceEntry ValidateAndPrepareCompletion(
        Guid activityInstanceId, string userId, ExpandoObject variables)
    {
        var metadata = _state.UserTasks.GetValueOrDefault(activityInstanceId)
            ?? throw new InvalidOperationException(
                $"Activity instance '{activityInstanceId}' is not a user task.");

        // Must be claimed by this user
        if (metadata.TaskState != UserTaskLifecycleState.Claimed)
            throw new InvalidOperationException("Task must be claimed before completing");
        if (metadata.ClaimedBy != userId)
            throw new InvalidOperationException(
                $"Task is claimed by {metadata.ClaimedBy}, not {userId}");

        // Validate expected output variables
        if (metadata.ExpectedOutputVariables is { Count: > 0 })
        {
            var dict = (IDictionary<string, object?>)variables;
            var missing = metadata.ExpectedOutputVariables.Where(v => !dict.ContainsKey(v)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Missing required output variables: {string.Join(", ", missing)}");
        }

        // Transition user task to Completed state before cleanup removes it
        metadata.Complete();

        return _state.GetActiveEntry(activityInstanceId);
    }
}
