using Fleans.Domain.States;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface IUserTaskRegistryGrain : IGrainWithIntegerKey
{
    ValueTask Register(UserTaskRegistration registration);
    ValueTask Unregister(Guid activityInstanceId);
    ValueTask UpdateClaim(Guid activityInstanceId, string? claimedBy,
        UserTaskLifecycleState taskState);

    [ReadOnly]
    ValueTask<IReadOnlyList<UserTaskRegistration>> GetPendingTasks(
        string? assignee = null, string? candidateGroup = null);

    [ReadOnly]
    ValueTask<UserTaskRegistration?> GetTask(Guid activityInstanceId);
}

[GenerateSerializer]
public record UserTaskRegistration(
    [property: Id(0)] Guid WorkflowInstanceId,
    [property: Id(1)] Guid ActivityInstanceId,
    [property: Id(2)] string ActivityId,
    [property: Id(3)] string? Assignee,
    [property: Id(4)] IReadOnlyList<string> CandidateGroups,
    [property: Id(5)] IReadOnlyList<string> CandidateUsers,
    [property: Id(6)] string? ClaimedBy,
    [property: Id(7)] UserTaskLifecycleState TaskState,
    [property: Id(8)] DateTimeOffset CreatedAt);
