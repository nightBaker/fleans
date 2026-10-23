namespace Fleans.Persistence.Events;

/// <summary>
/// One append to a workflow instance's journal: the events of a single <c>ConfirmEvents</c> batch,
/// versions <see cref="FirstVersion"/>..<see cref="LastVersion"/>, as one Orleans-binary
/// <c>List&lt;WorkflowEvent&gt;</c>. A row per batch instead of per event keeps the per-row and
/// per-index overhead off every event (#867). The key <c>(InstanceId, FirstVersion)</c> is also the
/// optimistic-concurrency check: two writers appending after the same version collide on it.
/// <see cref="BatchId"/> tells a retry of this same append from another writer's batch (#877).
/// </summary>
public class WorkflowEventBatchEntity
{
    public Guid InstanceId { get; set; }
    public int FirstVersion { get; set; }
    public int LastVersion { get; set; }

    /// <summary>
    /// Chosen by the writer once per batch and kept across its retries: a retry that collides with a
    /// row carrying its own id finds its batch already stored.
    /// </summary>
    public Guid BatchId { get; set; }

    public byte[] Payload { get; set; } = [];
}
