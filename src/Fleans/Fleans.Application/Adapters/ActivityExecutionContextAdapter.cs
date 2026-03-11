using Fleans.Domain;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Application.Adapters;

/// <summary>
/// Bridges <see cref="IActivityExecutionContext"/> to the enriched
/// <see cref="ActivityInstanceEntry"/> so that activities can execute
/// without direct grain calls. Domain events published by activities
/// are collected in <see cref="PublishedEvents"/> for the grain to
/// process after <c>ExecuteAsync</c> returns.
/// </summary>
[GenerateSerializer]
public class ActivityExecutionContextAdapter : IActivityExecutionContext
{
    [Id(0)]
    private readonly ActivityInstanceEntry _entry;
    [Id(1)]
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

    /// <summary>
    /// Whether the activity called <see cref="Complete"/> during execution.
    /// The grain uses this flag to decide whether to emit an
    /// <c>ActivityCompleted</c> domain event on the aggregate.
    /// </summary>
    [Id(2)]
    public bool WasCompleted { get; private set; }

    /// <summary>
    /// Whether the activity called <see cref="Execute"/> during execution.
    /// The grain uses this flag to decide whether to emit an
    /// <c>ActivityExecuting</c> domain event on the aggregate.
    /// </summary>
    [Id(3)]
    public bool WasExecuted { get; private set; }

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

    // --- State-changing methods ---

    public ValueTask SetMultiInstanceTotal(int total)
    {
        _entry.SetMultiInstanceTotal(total);
        return ValueTask.CompletedTask;
    }

    public ValueTask Complete()
    {
        _entry.Complete();
        WasCompleted = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask Execute()
    {
        _entry.Execute();
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
