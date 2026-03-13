using Fleans.Application.Grains;

namespace Fleans.Application;

public interface IUserTaskQueryService
{
    Task<IReadOnlyList<UserTaskRegistration>> GetPendingTasks(
        string? assignee = null, string? candidateGroup = null);

    Task<UserTaskRegistration?> GetTask(Guid activityInstanceId);
}
