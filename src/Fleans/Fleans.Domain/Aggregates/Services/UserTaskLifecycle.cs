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
        Guid activityInstanceId, string userId, IReadOnlyList<string> userGroups)
    {
        _state.GetActiveEntry(activityInstanceId);
        var metadata = _state.UserTasks.GetValueOrDefault(activityInstanceId)
            ?? throw new InvalidOperationException(
                $"Activity instance '{activityInstanceId}' is not a user task.");

        // Authorization: any-of-three "OR" model (#588). When no constraint is set,
        // the task is unrestricted. When any constraint is set, the caller must satisfy
        // at least one of: Assignee match, CandidateUsers contains userId, or
        // CandidateGroups intersects userGroups (ordinal case-sensitive).
        //
        // Rejection messages are deliberately identifier-free — they never enumerate
        // the task's Assignee or candidate sets. Audit detail lives in the structured
        // log (EventId 1066) where deployment-level filtering can be applied.
        var hasAssignee = metadata.Assignee is not null;
        var hasCandidateUsers = metadata.CandidateUsers.Count > 0;
        var hasCandidateGroups = metadata.CandidateGroups.Count > 0;
        var anyConstraint = hasAssignee || hasCandidateUsers || hasCandidateGroups;

        var matchesAssignee = hasAssignee && metadata.Assignee == userId;
        var matchesCandidateUsers = hasCandidateUsers && metadata.CandidateUsers.Contains(userId);
        var matchesCandidateGroups = hasCandidateGroups
            && userGroups.Any(g => metadata.CandidateGroups.Contains(g, StringComparer.Ordinal));

        if (anyConstraint && !(matchesAssignee || matchesCandidateUsers || matchesCandidateGroups))
            throw new InvalidOperationException(
                $"User {userId} is not authorized to claim this task");

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
