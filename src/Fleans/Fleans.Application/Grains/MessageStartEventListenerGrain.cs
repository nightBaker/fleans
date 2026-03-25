using System.Dynamic;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class MessageStartEventListenerGrain : Grain, IMessageStartEventListenerGrain
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<MessageStartEventListenerGrain> _logger;
    private readonly IPersistentState<MessageStartEventListenerState> _state;

    private MessageStartEventListenerState State => _state.State;

    public MessageStartEventListenerGrain(
        [PersistentState("state", GrainStorageNames.MessageStartEventListeners)] IPersistentState<MessageStartEventListenerState> state,
        IGrainFactory grainFactory,
        ILogger<MessageStartEventListenerGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async ValueTask RegisterProcess(string processDefinitionKey)
    {
        if (!State.AddProcess(processDefinitionKey))
        {
            LogProcessAlreadyRegistered(this.GetPrimaryKeyString(), processDefinitionKey);
            return;
        }

        await _state.WriteStateAsync();
        LogProcessRegistered(this.GetPrimaryKeyString(), processDefinitionKey);
    }

    public async ValueTask UnregisterProcess(string processDefinitionKey)
    {
        if (!State.RemoveProcess(processDefinitionKey))
        {
            LogProcessNotFound(this.GetPrimaryKeyString(), processDefinitionKey);
            return;
        }

        if (State.IsEmpty)
            await _state.ClearStateAsync();
        else
            await _state.WriteStateAsync();

        LogProcessUnregistered(this.GetPrimaryKeyString(), processDefinitionKey);
    }

    public async ValueTask<List<Guid>> FireMessageStartEvent(ExpandoObject variables)
    {
        var messageName = this.GetPrimaryKeyString();

        if (State.ProcessDefinitionKeys.Count == 0)
        {
            LogNoRegisteredProcesses(messageName);
            return [];
        }

        var tasks = State.ProcessDefinitionKeys.Select(async processDefinitionKey =>
        {
            try
            {
                var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(processDefinitionKey);

                // Guard: skip disabled processes to prevent race condition
                // between DisableProcess persisting IsActive=false and unregistering listeners
                if (!await processGrain.IsActive())
                {
                    LogProcessDisabledSkipped(messageName, processDefinitionKey);
                    return (Guid?)null;
                }

                var instanceId = Guid.NewGuid();
                var instance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);

                var definition = await processGrain.GetLatestDefinition();

                // Find the MessageStartEvent that matches this message name
                var messageStartActivityId = FindMessageStartActivityId(definition, messageName)
                    ?? throw new InvalidOperationException(
                        $"Message start activity for message '{messageName}' not found in process '{processDefinitionKey}'. " +
                        "The message definition may have been removed during a redeployment.");

                await instance.SetWorkflow(definition, messageStartActivityId);
                await instance.SetInitialVariables(variables);
                await instance.StartWorkflow();

                LogMessageStartEventFired(messageName, processDefinitionKey, instanceId);
                return (Guid?)instanceId;
            }
            catch (Exception ex)
            {
                LogMessageStartEventFailed(messageName, processDefinitionKey, ex);
                return (Guid?)null;
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(id => id.HasValue).Select(id => id!.Value).ToList();
    }

    private static string? FindMessageStartActivityId(IWorkflowDefinition definition, string messageName)
    {
        foreach (var activity in definition.Activities.OfType<MessageStartEvent>())
        {
            var msgDef = definition.FindMessageDefinition(activity.MessageDefinitionId);
            if (msgDef?.Name == messageName)
                return activity.ActivityId;
        }
        return null;
    }

    [LoggerMessage(EventId = 9100, Level = LogLevel.Information, Message = "Registered process {ProcessDefinitionKey} for message start event '{MessageName}'")]
    private partial void LogProcessRegistered(string messageName, string processDefinitionKey);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Information, Message = "Unregistered process {ProcessDefinitionKey} from message start event '{MessageName}'")]
    private partial void LogProcessUnregistered(string messageName, string processDefinitionKey);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Information, Message = "Message start event fired for '{MessageName}', process {ProcessDefinitionKey}, created instance {InstanceId}")]
    private partial void LogMessageStartEventFired(string messageName, string processDefinitionKey, Guid instanceId);

    [LoggerMessage(EventId = 9103, Level = LogLevel.Warning, Message = "No registered processes for message start event '{MessageName}'")]
    private partial void LogNoRegisteredProcesses(string messageName);

    [LoggerMessage(EventId = 9104, Level = LogLevel.Error, Message = "Failed to start workflow for message '{MessageName}', process {ProcessDefinitionKey}")]
    private partial void LogMessageStartEventFailed(string messageName, string processDefinitionKey, Exception ex);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Debug, Message = "Process {ProcessDefinitionKey} already registered for message start event '{MessageName}', skipping write")]
    private partial void LogProcessAlreadyRegistered(string messageName, string processDefinitionKey);

    [LoggerMessage(EventId = 9106, Level = LogLevel.Debug, Message = "Process {ProcessDefinitionKey} not found for message start event '{MessageName}', skipping unregister")]
    private partial void LogProcessNotFound(string messageName, string processDefinitionKey);

    [LoggerMessage(EventId = 9107, Level = LogLevel.Warning, Message = "Skipping disabled process {ProcessDefinitionKey} for message '{MessageName}'")]
    private partial void LogProcessDisabledSkipped(string messageName, string processDefinitionKey);
}
