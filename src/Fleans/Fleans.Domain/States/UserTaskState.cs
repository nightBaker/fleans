namespace Fleans.Domain.States;

[GenerateSerializer]
public class UserTaskState
{
    [Id(0)] public Guid ActivityInstanceId { get; set; }
    [Id(1)] public Guid WorkflowInstanceId { get; set; }
    [Id(2)] public string ActivityId { get; set; } = "";
    [Id(3)] public string? Assignee { get; set; }
    [Id(4)] public IReadOnlyList<string> CandidateGroups { get; set; } = [];
    [Id(5)] public IReadOnlyList<string> CandidateUsers { get; set; } = [];
    [Id(6)] public IReadOnlyList<string>? ExpectedOutputVariables { get; set; }
    [Id(7)] public string? ClaimedBy { get; set; }
    [Id(8)] public DateTimeOffset? ClaimedAt { get; set; }
    [Id(9)] public UserTaskLifecycleState TaskState { get; set; }
    [Id(10)] public DateTimeOffset CreatedAt { get; set; }
    [Id(11)] public string ETag { get; set; } = "";
}
