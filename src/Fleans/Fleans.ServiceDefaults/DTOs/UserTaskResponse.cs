namespace Fleans.ServiceDefaults.DTOs;

public record UserTaskResponse(
    Guid WorkflowInstanceId,
    Guid ActivityInstanceId,
    string ActivityId,
    string? Assignee,
    IReadOnlyList<string> CandidateGroups,
    IReadOnlyList<string> CandidateUsers,
    string? ClaimedBy,
    string TaskState,
    DateTimeOffset CreatedAt);
