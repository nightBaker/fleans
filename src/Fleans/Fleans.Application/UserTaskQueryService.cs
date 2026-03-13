using Fleans.Application.Grains;

namespace Fleans.Application;

public class UserTaskQueryService : IUserTaskQueryService
{
    private const int RegistrySingletonKey = 0;

    private readonly IGrainFactory _grainFactory;

    public UserTaskQueryService(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    public async Task<IReadOnlyList<UserTaskRegistration>> GetPendingTasks(
        string? assignee = null, string? candidateGroup = null)
    {
        var registry = _grainFactory.GetGrain<IUserTaskRegistryGrain>(RegistrySingletonKey);
        return await registry.GetPendingTasks(assignee, candidateGroup);
    }

    public async Task<UserTaskRegistration?> GetTask(Guid activityInstanceId)
    {
        var registry = _grainFactory.GetGrain<IUserTaskRegistryGrain>(RegistrySingletonKey);
        return await registry.GetTask(activityInstanceId);
    }
}
