namespace Fleans.Persistence.Events;

public class WorkflowEventEntity
{
    public long Id { get; set; }
    public string GrainId { get; set; } = "";
    public int Version { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
}

public class WorkflowSnapshotEntity
{
    public string GrainId { get; set; } = "";
    public int Version { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
