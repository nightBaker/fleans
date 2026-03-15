using Fleans.Domain.Poc;
using Orleans.EventSourcing;

namespace Fleans.Application.Grains.Poc;

public class JournaledCounterGrain : JournaledGrain<CounterState, ICounterEvent>, IJournaledCounterGrain
{
    private CounterAggregate _aggregate = null!;
    private bool _draining;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // State is already recovered via TransitionState replay at this point
        _aggregate = new CounterAggregate(State);
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task Increment(int amount)
    {
        _aggregate.Increment(amount);
        await DrainToJournal();
    }

    public async Task Decrement(int amount)
    {
        _aggregate.Decrement(amount);
        await DrainToJournal();
    }

    public async Task Reset()
    {
        _aggregate.Reset();
        await DrainToJournal();
    }

    public Task<int> GetValue() => Task.FromResult(State.Value);

    public Task<int> GetVersion() => Task.FromResult(Version);

    public Task<int> GetEventCount() => Task.FromResult(State.EventCount);

    public Task Deactivate()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    protected override void TransitionState(CounterState state, ICounterEvent @event)
    {
        // During drain, the aggregate has already applied events to State.
        // Only apply during replay (grain activation) when _draining is false.
        if (!_draining)
            state.Apply(@event);
    }

    private async Task DrainToJournal()
    {
        _draining = true;
        var events = _aggregate.GetUncommittedEvents();
        foreach (var e in events)
        {
            RaiseEvent(e);
        }

        await ConfirmEvents();
        _aggregate.ClearUncommittedEvents();
        _draining = false;
    }
}
