namespace Fleans.Persistence.Entities;

public class ActivityInstanceEntryEntity
{
    public Guid ActivityInstanceId { get; set; }
    public string ActivityId { get; set; } = null!;
    public Guid WorkflowInstanceId { get; set; }
    public bool IsCompleted { get; set; }
}
