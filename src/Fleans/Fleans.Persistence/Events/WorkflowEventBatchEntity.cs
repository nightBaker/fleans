namespace Fleans.Persistence.Events;

/// <summary>
/// One append to a workflow instance's journal: the events of a single <c>ConfirmEvents</c> batch,
/// versions <see cref="FirstVersion"/>..<see cref="LastVersion"/>, as one Orleans-binary
/// <c>List&lt;WorkflowEvent&gt;</c>. A row per batch instead of per event keeps the per-row and
/// per-index overhead off every event (#867). The key <c>(InstanceId, FirstVersion)</c> is also the
/// optimistic-concurrency check: two writers appending after the same version collide on it.
/// </summary>
public class WorkflowEventBatchEntity
{
    public Guid InstanceId { get; set; }
    public int FirstVersion { get; set; }
    public int LastVersion { get; set; }
    public byte[] Payload { get; set; } = [];
}
