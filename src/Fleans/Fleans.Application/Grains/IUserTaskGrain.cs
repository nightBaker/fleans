using Fleans.Domain.States;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface IUserTaskGrain : IGrainWithGuidKey
{
    Task Register(Guid workflowInstanceId, string activityId,
        string? assignee, IReadOnlyList<string> candidateGroups,
        IReadOnlyList<string> candidateUsers, IReadOnlyList<string>? expectedOutputVariables);

    /// <summary>
    /// Replaces the task's element id and assignment after an instance migration (#1069). The
    /// claim and lifecycle state stay; a task that was never registered is left alone.
    /// </summary>
    Task Redefine(string activityId, string? assignee, IReadOnlyList<string> candidateGroups,
        IReadOnlyList<string> candidateUsers, IReadOnlyList<string>? expectedOutputVariables);

    Task UpdateClaim(string? claimedBy, UserTaskLifecycleState taskState);

    Task MarkCompleted();

    /// <summary>Removes the task record, if any. Used when registration ultimately failed.</summary>
    Task Discard();

    [ReadOnly]
    Task<UserTaskState?> GetState();
}
