namespace Fleans.Persistence.Entities;

public class ActivityInstanceEntity
{
    public Guid Id { get; set; }
    public string? ActivityId { get; set; }
    public string? ActivityType { get; set; }
    public bool IsExecuting { get; set; }
    public bool IsCompleted { get; set; }
    public Guid VariablesId { get; set; }
    public int? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? ExecutionStartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ETag { get; set; }
}
