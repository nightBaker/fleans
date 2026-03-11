using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class SignalStartEventListenerGrain : Grain, ISignalStartEventListenerGrain
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<SignalStartEventListenerGrain> _logger;
    private readonly IPersistentState<SignalStartEventListenerState> _state;

    private SignalStartEventListenerState State => _state.State;

    public SignalStartEventListenerGrain(
        [PersistentState("state", GrainStorageNames.SignalStartEventListeners)] IPersistentState<SignalStartEventListenerState> state,
        IGrainFactory grainFactory,
        ILogger<SignalStartEventListenerGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async ValueTask RegisterProcess(string processDefinitionKey)
    {
        State.AddProcess(processDefinitionKey);
        await _state.WriteStateAsync();
        LogProcessRegistered(this.GetPrimaryKeyString(), processDefinitionKey);
    }

    public async ValueTask UnregisterProcess(string processDefinitionKey)
    {
        if (!State.RemoveProcess(processDefinitionKey))
        {
            LogProcessUnregistered(this.GetPrimaryKeyString(), processDefinitionKey);
            return;
        }

        if (State.IsEmpty)
            await _state.ClearStateAsync();
        else
            await _state.WriteStateAsync();

        LogProcessUnregistered(this.GetPrimaryKeyString(), processDefinitionKey);
    }

    public async ValueTask<List<Guid>> FireSignalStartEvent()
    {
        var signalName = this.GetPrimaryKeyString();
        var createdIds = new List<Guid>();

        if (State.ProcessDefinitionKeys.Count == 0)
        {
            LogNoRegisteredProcesses(signalName);
            return createdIds;
        }

        var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

        foreach (var processDefinitionKey in State.ProcessDefinitionKeys)
        {
            try
            {
                var instanceId = Guid.NewGuid();
                var instance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);

                var definition = await factory.GetLatestWorkflowDefinition(processDefinitionKey);

                var signalStartActivityId = FindSignalStartActivityId(definition, signalName)
                    ?? throw new InvalidOperationException(
                        $"Signal start activity for signal '{signalName}' not found in process '{processDefinitionKey}'. " +
                        "The signal definition may have been removed during a redeployment.");

                await instance.SetWorkflow(definition, signalStartActivityId);
                await instance.StartWorkflow();

                createdIds.Add(instanceId);
                LogSignalStartEventFired(signalName, processDefinitionKey, instanceId);
            }
            catch (Exception ex)
            {
                LogSignalStartEventFailed(signalName, processDefinitionKey, ex);
            }
        }

        return createdIds;
    }

    private static string? FindSignalStartActivityId(IWorkflowDefinition definition, string signalName)
    {
        foreach (var activity in definition.Activities.OfType<SignalStartEvent>())
        {
            var sigDef = definition.Signals.FirstOrDefault(s => s.Id == activity.SignalDefinitionId);
            if (sigDef?.Name == signalName)
                return activity.ActivityId;
        }
        return null;
    }

    [LoggerMessage(EventId = 9200, Level = LogLevel.Information, Message = "Registered process {ProcessDefinitionKey} for signal start event '{SignalName}'")]
    private partial void LogProcessRegistered(string signalName, string processDefinitionKey);

    [LoggerMessage(EventId = 9201, Level = LogLevel.Information, Message = "Unregistered process {ProcessDefinitionKey} from signal start event '{SignalName}'")]
    private partial void LogProcessUnregistered(string signalName, string processDefinitionKey);

    [LoggerMessage(EventId = 9202, Level = LogLevel.Information, Message = "Signal start event fired for '{SignalName}', process {ProcessDefinitionKey}, created instance {InstanceId}")]
    private partial void LogSignalStartEventFired(string signalName, string processDefinitionKey, Guid instanceId);

    [LoggerMessage(EventId = 9203, Level = LogLevel.Warning, Message = "No registered processes for signal start event '{SignalName}'")]
    private partial void LogNoRegisteredProcesses(string signalName);

    [LoggerMessage(EventId = 9204, Level = LogLevel.Error, Message = "Failed to start workflow for signal '{SignalName}', process {ProcessDefinitionKey}")]
    private partial void LogSignalStartEventFailed(string signalName, string processDefinitionKey, Exception ex);
}
