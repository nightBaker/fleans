namespace Fleans.Domain.States;

/// <summary>
/// Subscriptions waiting for one signal name in one bucket: a signal's subscriptions are spread
/// over several buckets by workflow instance id, one <c>SignalCorrelationGrain</c> each.
/// Mutate only through the domain methods: besides
/// the list they record what changed since the last successful write
/// (<see cref="AddedSubscriptions"/>, <see cref="RemovedSubscriptionKeys"/>,
/// <see cref="SubscriptionsCleared"/>), so the EF storage writes only the affected rows instead
/// of loading and diffing all N subscriptions (#859). The change set is neither serialized nor
/// mapped; the grain calls <see cref="AcceptChanges"/> after a successful write.
/// </summary>
[GenerateSerializer]
public class SignalCorrelationState
{
    [Id(0)] public string SignalName { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] private List<SignalSubscription> _subscriptions = new();
    [Id(3)] public int Bucket { get; set; }

    // Change set since the last AcceptChanges — NOT serialized by Orleans, NOT mapped by EF Core.
    // Created lazily: a deserializer may bypass the constructor and field initializers.
    [NonSerialized]
    private Dictionary<(Guid WorkflowInstanceId, string ActivityId, Guid HostActivityInstanceId), SignalSubscription>? _added;
    [NonSerialized]
    private HashSet<(Guid WorkflowInstanceId, string ActivityId, Guid HostActivityInstanceId)>? _removed;
    [NonSerialized]
    private bool _cleared;

    private Dictionary<(Guid WorkflowInstanceId, string ActivityId, Guid HostActivityInstanceId), SignalSubscription> Added => _added ??= new();
    private HashSet<(Guid WorkflowInstanceId, string ActivityId, Guid HostActivityInstanceId)> Removed => _removed ??= new();

    public IReadOnlyList<SignalSubscription> Subscriptions => _subscriptions;

    /// <summary>Subscriptions added since the last <see cref="AcceptChanges"/>.</summary>
    public IReadOnlyCollection<SignalSubscription> AddedSubscriptions => Added.Values;

    /// <summary>
    /// Keys removed since the last <see cref="AcceptChanges"/>. Storage deletes these before it
    /// inserts <see cref="AddedSubscriptions"/>, so a key removed and re-added is replaced.
    /// </summary>
    public IReadOnlyCollection<(Guid WorkflowInstanceId, string ActivityId, Guid HostActivityInstanceId)> RemovedSubscriptionKeys => Removed;

    /// <summary>
    /// True when every subscription was removed since the last <see cref="AcceptChanges"/>;
    /// storage applies it first, as one set-based delete.
    /// </summary>
    public bool SubscriptionsCleared => _cleared;

    /// <summary>Adds the subscription; returns false if one with the same key already exists.</summary>
    public bool AddSubscription(SignalSubscription subscription)
    {
        // Keyed by the activity instance too: iterations of a multi-instance body and passes of a
        // loop share the ActivityId but wait on their own (#853).
        var key = (subscription.WorkflowInstanceId, subscription.ActivityId, subscription.HostActivityInstanceId);
        if (_subscriptions.Any(s => s.WorkflowInstanceId == key.WorkflowInstanceId && s.ActivityId == key.ActivityId
                                    && s.HostActivityInstanceId == key.HostActivityInstanceId))
            return false;

        _subscriptions.Add(subscription);
        Added[key] = subscription;
        return true;
    }

    /// <summary>Removes the subscription with this key; returns false if there was none.</summary>
    public bool RemoveSubscription(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId)
    {
        var removed = _subscriptions.RemoveAll(s =>
            s.WorkflowInstanceId == workflowInstanceId && s.ActivityId == activityId
            && s.HostActivityInstanceId == hostActivityInstanceId);
        if (removed == 0)
            return false;

        var key = (workflowInstanceId, activityId, hostActivityInstanceId);
        Added.Remove(key);
        Removed.Add(key);
        return true;
    }

    /// <summary>Removes and returns every subscription.</summary>
    public List<SignalSubscription> TakeAllSubscriptions()
    {
        var all = _subscriptions.ToList();
        _subscriptions.Clear();
        _added?.Clear();
        _removed?.Clear();
        _cleared = true;
        return all;
    }

    /// <summary>Forgets the change set once storage has written it.</summary>
    public void AcceptChanges()
    {
        _added?.Clear();
        _removed?.Clear();
        _cleared = false;
    }
}

[GenerateSerializer]
public record SignalSubscription(
    [property: Id(0)] Guid WorkflowInstanceId,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] Guid HostActivityInstanceId)
{
    [Id(3)] public string SignalName { get; init; } = string.Empty;

    /// <summary>The bucket of <see cref="SignalName"/> holding this subscription; set by storage.</summary>
    [Id(4)] public int Bucket { get; init; }
}
