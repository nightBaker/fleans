namespace Fleans.Domain.Poc;

/// <summary>
/// Mirrors the WorkflowExecution aggregate's Emit/Apply pattern.
/// The aggregate owns domain logic, accumulates uncommitted events,
/// and the grain drains them to JournaledGrain's RaiseEvent/ConfirmEvents.
/// </summary>
public class CounterAggregate
{
    private readonly CounterState _state;
    private readonly List<ICounterEvent> _uncommittedEvents = [];

    public CounterAggregate(CounterState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public int Value => _state.Value;
    public int EventCount => _state.EventCount;

    public void Increment(int amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        Emit(new CounterIncremented(amount));
    }

    public void Decrement(int amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        Emit(new CounterDecremented(amount));
    }

    public void Reset() => Emit(new CounterReset());

    public IReadOnlyList<ICounterEvent> GetUncommittedEvents() => _uncommittedEvents.AsReadOnly();

    public void ClearUncommittedEvents() => _uncommittedEvents.Clear();

    private void Emit(ICounterEvent @event)
    {
        _state.Apply(@event);
        _uncommittedEvents.Add(@event);
    }
}
