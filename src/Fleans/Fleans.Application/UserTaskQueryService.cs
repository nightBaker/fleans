using Fleans.Application.Grains;
using Fleans.Application.DTOs;

namespace Fleans.Application;

public class UserTaskQueryService : IUserTaskQueryService
{
    private const int RegistrySingletonKey = 0;

    private readonly IGrainFactory _grainFactory;

    public UserTaskQueryService(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    public async Task<IReadOnlyList<UserTaskResponse>> GetPendingTasks(
        string? assignee = null, string? candidateGroup = null)
    {
        var registry = _grainFactory.GetGrain<IUserTaskRegistryGrain>(RegistrySingletonKey);
        var tasks = await registry.GetPendingTasks(assignee, candidateGroup);
        return tasks.Select(ToDto).ToList();
    }

    public async Task<UserTaskResponse?> GetTask(Guid activityInstanceId)
    {
        var registry = _grainFactory.GetGrain<IUserTaskRegistryGrain>(RegistrySingletonKey);
        var task = await registry.GetTask(activityInstanceId);
        return task is null ? null : ToDto(task);
    }

    private static UserTaskResponse ToDto(UserTaskRegistration t) =>
        new(t.WorkflowInstanceId, t.ActivityInstanceId, t.ActivityId,
            t.Assignee, t.CandidateGroups, t.CandidateUsers,
            t.ClaimedBy, t.TaskState.ToString(), t.CreatedAt);
}
