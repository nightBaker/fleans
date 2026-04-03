using System.Dynamic;
using Fleans.Domain;
using Fleans.Domain.States;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public abstract class StartEventListenerGrainBase<TState> : Grain
    where TState : class, IStartEventListenerState
{
    private readonly IGrainFactory _grainFactory;
    private readonly IPersistentState<TState> _state;

    protected TState State => _state.State;

    protected StartEventListenerGrainBase(
        IPersistentState<TState> state,
        IGrainFactory grainFactory)
    {
        _state = state;
        _grainFactory = grainFactory;
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

    protected async ValueTask<List<Guid>> FireStartEventCore(ExpandoObject? variables)
    {
        var eventName = this.GetPrimaryKeyString();

        if (State.ProcessDefinitionKeys.Count == 0)
        {
            OnNoRegisteredProcesses(eventName);
            return [];
        }

        var tasks = State.ProcessDefinitionKeys.Select(async processDefinitionKey =>
        {
            try
            {
                var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(processDefinitionKey);

                if (!await processGrain.IsActive())
                {
                    OnProcessDisabledSkipped(eventName, processDefinitionKey);
                    return (Guid?)null;
                }

                var instanceId = Guid.NewGuid();
                var instance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);

                var definition = await processGrain.GetLatestDefinition();

                var startActivityId = FindStartActivityId(definition, eventName)
                    ?? throw new InvalidOperationException(
                        $"Start activity for '{eventName}' not found in process '{processDefinitionKey}'. " +
                        "The definition may have been removed during a redeployment.");

                await instance.SetWorkflow(definition, startActivityId);

                if (variables is not null)
                    await instance.SetInitialVariables(variables);

                await instance.StartWorkflow();

                OnStartEventFired(eventName, processDefinitionKey, instanceId);
                return (Guid?)instanceId;
            }
            catch (Exception ex)
            {
                OnStartEventFailed(eventName, processDefinitionKey, ex);
                return (Guid?)null;
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(id => id.HasValue).Select(id => id!.Value).ToList();
    }

    protected abstract string? FindStartActivityId(IWorkflowDefinition definition, string eventName);

    protected abstract void OnProcessRegistered(string eventName, string processDefinitionKey);
    protected abstract void OnProcessUnregistered(string eventName, string processDefinitionKey);
    protected abstract void OnProcessAlreadyRegistered(string eventName, string processDefinitionKey);
    protected abstract void OnProcessNotFound(string eventName, string processDefinitionKey);
    protected abstract void OnNoRegisteredProcesses(string eventName);
    protected abstract void OnProcessDisabledSkipped(string eventName, string processDefinitionKey);
    protected abstract void OnStartEventFired(string eventName, string processDefinitionKey, Guid instanceId);
    protected abstract void OnStartEventFailed(string eventName, string processDefinitionKey, Exception ex);
}
