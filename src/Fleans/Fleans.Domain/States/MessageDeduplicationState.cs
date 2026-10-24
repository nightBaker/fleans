namespace Fleans.Domain.States;

/// <summary>
/// What one message sent with an idempotency key did (#743), so a retry of the same send returns
/// that outcome instead of doing it again. Persisted by <c>MessageDeduplicationGrain</c> — one grain
/// and one <c>MessageDeduplications</c> row per message name + idempotency key — and mutated only
/// through the methods below:
/// <code>
/// None ─► Correlated                       (delivered to a running instance)
///  └────► Starting ─► Started              (instances planned, then started)
///             └─────► None                 (nothing started: a retry may try again)
/// </code>
/// The instance ids are planned and persisted <em>before</em> any start, so a send interrupted
/// between its starts and the final write resumes with the same ids — a repeated
/// <c>StartWorkflow</c> of an instance already started is a no-op. A record counts for
/// <see cref="Window"/> after the first send; after that the key starts afresh.
/// </summary>
[GenerateSerializer]
public class MessageDeduplicationState
{
    /// <summary>How long a recorded send answers retries of the same key.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    [Id(0)] public string Key { get; set; } = "";
    [Id(1)] public MessageDeduplicationStatus Status { get; set; }
    [Id(2)] public DateTimeOffset ReceivedAt { get; set; }
    /// <summary>The instance each subscribed process gets, chosen before the first start.</summary>
    [Id(3)] public List<PlannedStart> PlannedStarts { get; set; } = new();
    /// <summary>The instances that did start — what a retry returns.</summary>
    [Id(4)] public List<Guid> StartedInstanceIds { get; set; } = new();
    [Id(5)] public string? ETag { get; set; }

    /// <summary>Whether a send within the last <see cref="Window"/> left an outcome to return.</summary>
    public bool IsRecorded(DateTimeOffset now)
        => Status != MessageDeduplicationStatus.None && now - ReceivedAt < Window;

    /// <summary>The message was delivered to the running instance subscribed to its correlation key.</summary>
    public void RecordCorrelated(DateTimeOffset at)
    {
        Reset(at);
        Status = MessageDeduplicationStatus.Correlated;
    }

    /// <summary>Records the instance ids the message's start events will use, before starting any.</summary>
    public void PlanStarts(IReadOnlyCollection<PlannedStart> starts, DateTimeOffset at)
    {
        if (starts.Count == 0)
            throw new ArgumentException("A message start plan needs at least one process.", nameof(starts));

        Reset(at);
        Status = MessageDeduplicationStatus.Starting;
        PlannedStarts = starts.ToList();
    }

    /// <summary>
    /// Records the instances that started. When none did (every process disabled or failing), the
    /// record is dropped instead, so a retry is a fresh send rather than a recorded "not delivered".
    /// </summary>
    public void CompleteStarts(IReadOnlyCollection<Guid> startedInstanceIds)
    {
        if (Status != MessageDeduplicationStatus.Starting)
            throw new InvalidOperationException($"No message start is in progress (status {Status}).");

        if (startedInstanceIds.Count == 0)
        {
            Reset(ReceivedAt);
            return;
        }

        Status = MessageDeduplicationStatus.Started;
        StartedInstanceIds = startedInstanceIds.ToList();
    }

    private void Reset(DateTimeOffset at)
    {
        Status = MessageDeduplicationStatus.None;
        ReceivedAt = at;
        PlannedStarts = new List<PlannedStart>();
        StartedInstanceIds = new List<Guid>();
    }
}

public enum MessageDeduplicationStatus
{
    None = 0,
    Correlated = 1,
    Starting = 2,
    Started = 3
}

/// <summary>The instance id one process gets from a start event, chosen before it starts.</summary>
[GenerateSerializer]
public record PlannedStart(
    [property: Id(0)] string ProcessDefinitionKey,
    [property: Id(1)] Guid InstanceId);
