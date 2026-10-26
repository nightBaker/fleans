using Fleans.Domain.States;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

/// <summary>
/// The persisted registrations of one message or signal start event name: which process keys it
/// starts. Read by every host's <see cref="StartEvents.StartEventSubscriptionCache"/> on a miss —
/// the start itself never passes through this grain (#858).
/// </summary>
public abstract class StartEventListenerGrainBase<TState> : Grain
    where TState : class, IStartEventListenerState
{
    private readonly IPersistentState<TState> _state;

    protected TState State => _state.State;

    protected StartEventListenerGrainBase(IPersistentState<TState> state)
    {
        _state = state;
    }

    public async ValueTask RegisterProcess(string processDefinitionKey)
    {
        var eventName = this.GetPrimaryKeyString();

        if (!State.AddProcess(processDefinitionKey))
        {
            OnProcessAlreadyRegistered(eventName, processDefinitionKey);
            return;
        }

        await _state.WriteStateAsync();
        OnProcessRegistered(eventName, processDefinitionKey);
    }

    public async ValueTask UnregisterProcess(string processDefinitionKey)
    {
        var eventName = this.GetPrimaryKeyString();

        if (!State.RemoveProcess(processDefinitionKey))
        {
            OnProcessNotFound(eventName, processDefinitionKey);
            return;
        }

        if (State.IsEmpty)
            await _state.ClearStateAsync();
        else
            await _state.WriteStateAsync();

        OnProcessUnregistered(eventName, processDefinitionKey);
    }

    public ValueTask<List<string>> GetProcessKeys() => ValueTask.FromResult(State.ProcessDefinitionKeys.ToList());

    protected abstract void OnProcessRegistered(string eventName, string processDefinitionKey);
    protected abstract void OnProcessUnregistered(string eventName, string processDefinitionKey);
    protected abstract void OnProcessAlreadyRegistered(string eventName, string processDefinitionKey);
    protected abstract void OnProcessNotFound(string eventName, string processDefinitionKey);
}
