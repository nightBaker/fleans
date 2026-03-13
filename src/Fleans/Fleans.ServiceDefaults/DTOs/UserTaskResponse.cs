namespace Fleans.ServiceDefaults.DTOs;

public record UserTaskResponse(
    Guid WorkflowInstanceId,
    Guid ActivityInstanceId,
    string ActivityId,
    string? Assignee,
    List<string> CandidateGroups,
    List<string> CandidateUsers,
    string? ClaimedBy,
    string TaskState,
    DateTimeOffset CreatedAt);
