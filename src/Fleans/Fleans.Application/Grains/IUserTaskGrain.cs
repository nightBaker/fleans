using Fleans.Domain.States;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface IUserTaskGrain : IGrainWithGuidKey
{
    Task Register(Guid workflowInstanceId, string activityId,
        string? assignee, IReadOnlyList<string> candidateGroups,
        IReadOnlyList<string> candidateUsers, IReadOnlyList<string>? expectedOutputVariables);

    Task UpdateClaim(string? claimedBy, UserTaskLifecycleState taskState);

    Task MarkCompleted();

    [ReadOnly]
    Task<UserTaskState?> GetState();
}
