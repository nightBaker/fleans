namespace Fleans.Persistence.Events;

/// <summary>
/// The latest snapshot of a workflow instance: its full state (Orleans binary serialization) after
/// the first <see cref="Version"/> events. A cache over the journal — never the source of truth.
/// </summary>
public class WorkflowSnapshotEntity
{
    public Guid InstanceId { get; set; }
    public int Version { get; set; }
    public byte[] Payload { get; set; } = [];
    public DateTimeOffset Timestamp { get; set; }
}
