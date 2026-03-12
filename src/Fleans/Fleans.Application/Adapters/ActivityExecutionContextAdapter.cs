using Fleans.Domain;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Application.Adapters;

/// <summary>
/// Bridges <see cref="IActivityExecutionContext"/> to the enriched
/// <see cref="ActivityInstanceEntry"/> so that activities can execute
/// without direct grain calls. The adapter does NOT mutate state directly —
/// it collects intent flags that the grain reads after <c>ExecuteAsync</c>
/// returns, then routes all state changes through the aggregate's Emit/Apply path.
/// Also used as a read-only wrapper when returning activities via grain methods
/// (GetActiveActivities/GetCompletedActivities), hence requires [GenerateSerializer].
/// </summary>
[GenerateSerializer]
public class ActivityExecutionContextAdapter : IActivityExecutionContext
{
    [Id(0)]
    private readonly ActivityInstanceEntry _entry;
    [Id(4)]
    private readonly List<IDomainEvent> _publishedEvents = [];

    public ActivityExecutionContextAdapter(ActivityInstanceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entry = entry;
    }

    /// <summary>
    /// Domain events published by the activity during execution.
    /// The grain reads this list after <c>ExecuteAsync</c> returns.
    /// </summary>
    public IReadOnlyList<IDomainEvent> PublishedEvents => _publishedEvents.AsReadOnly();

    [Id(1)]
    public bool WasCompleted { get; private set; }
    [Id(2)]
    public bool WasExecuted { get; private set; }
    [Id(3)]
    public int? PendingMultiInstanceTotal { get; private set; }

    // --- Read-only delegates to entry fields ---

    public ValueTask<Guid> GetActivityInstanceId()
        => ValueTask.FromResult(_entry.ActivityInstanceId);

    public ValueTask<string> GetActivityId()
        => ValueTask.FromResult(_entry.ActivityId);

    public ValueTask<Guid> GetVariablesStateId()
        => ValueTask.FromResult(_entry.VariablesId);

    public ValueTask<int?> GetMultiInstanceIndex()
        => ValueTask.FromResult(_entry.MultiInstanceIndex);

    public ValueTask<int?> GetMultiInstanceTotal()
        => ValueTask.FromResult(_entry.MultiInstanceTotal);

    public ValueTask<bool> IsCompleted()
        => ValueTask.FromResult(_entry.IsCompleted);

    public ValueTask<Guid?> GetTokenId()
        => ValueTask.FromResult(_entry.TokenId);

    // --- Intent-collecting methods (no direct state mutation) ---

    public ValueTask SetMultiInstanceTotal(int total)
    {
        PendingMultiInstanceTotal = total;
        return ValueTask.CompletedTask;
    }

    public ValueTask Complete()
    {
        WasCompleted = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask Execute()
    {
        WasExecuted = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _publishedEvents.Add(domainEvent);
        return ValueTask.CompletedTask;
    }
}
