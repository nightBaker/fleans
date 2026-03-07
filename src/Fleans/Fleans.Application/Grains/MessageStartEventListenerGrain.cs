using System.Dynamic;
using Fleans.Application.WorkflowFactory;
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
        State.AddProcess(processDefinitionKey);
        await _state.WriteStateAsync();
        LogProcessRegistered(this.GetPrimaryKeyString(), processDefinitionKey);
    }

    public async ValueTask UnregisterProcess(string processDefinitionKey)
    {
        State.RemoveProcess(processDefinitionKey);
        await _state.WriteStateAsync();

        if (State.IsEmpty)
            await _state.ClearStateAsync();

        LogProcessUnregistered(this.GetPrimaryKeyString(), processDefinitionKey);
    }

    public async ValueTask<List<Guid>> FireMessageStartEvent(ExpandoObject variables)
    {
        var messageName = this.GetPrimaryKeyString();
        var createdIds = new List<Guid>();

        if (State.ProcessDefinitionKeys.Count == 0)
        {
            LogNoRegisteredProcesses(messageName);
            return createdIds;
        }

        var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

        foreach (var processDefinitionKey in State.ProcessDefinitionKeys)
        {
            var instanceId = Guid.NewGuid();
            var instance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);

            var definition = await factory.GetLatestWorkflowDefinition(processDefinitionKey);

            // Find the MessageStartEvent that matches this message name
            var messageStartActivityId = FindMessageStartActivityId(definition, messageName);

            await instance.SetWorkflow(definition, messageStartActivityId);
            await instance.SetInitialVariables(variables);
            await instance.StartWorkflow();

            createdIds.Add(instanceId);
            LogMessageStartEventFired(messageName, processDefinitionKey, instanceId);
        }

        return createdIds;
    }

    private static string? FindMessageStartActivityId(IWorkflowDefinition definition, string messageName)
    {
        foreach (var activity in definition.Activities.OfType<MessageStartEvent>())
        {
            var msgDef = definition.Messages.FirstOrDefault(m => m.Id == activity.MessageDefinitionId);
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
}
