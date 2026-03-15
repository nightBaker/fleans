using Fleans.Application.DTOs;

namespace Fleans.Application;

public interface IUserTaskQueryService
{
    Task<IReadOnlyList<UserTaskResponse>> GetPendingTasks(
        string? assignee = null, string? candidateGroup = null);

    Task<UserTaskResponse?> GetTask(Guid activityInstanceId);
}
