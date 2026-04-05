using System.Dynamic;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class MessageStartEventListenerGrain :
    StartEventListenerGrainBase<MessageStartEventListenerState>, IMessageStartEventListenerGrain
{
    private readonly ILogger<MessageStartEventListenerGrain> _logger;

    public MessageStartEventListenerGrain(
        [PersistentState("state", GrainStorageNames.MessageStartEventListeners)] IPersistentState<MessageStartEventListenerState> state,
        IGrainFactory grainFactory,
        ILogger<MessageStartEventListenerGrain> logger)
        : base(state, grainFactory)
    {
        _logger = logger;
    }

    public async ValueTask<List<Guid>> FireMessageStartEvent(ExpandoObject variables)
        => await FireStartEventCore(variables);

    protected override string? FindStartActivityId(IWorkflowDefinition definition, string eventName)
    {
        foreach (var activity in definition.Activities.OfType<MessageStartEvent>())
        {
            var msgDef = definition.FindMessageDefinition(activity.MessageDefinitionId);
            if (msgDef?.Name == eventName)
                return activity.ActivityId;
        }
        return null;
    }

    protected override void OnProcessRegistered(string eventName, string processDefinitionKey)
        => LogProcessRegistered(eventName, processDefinitionKey);
    protected override void OnProcessUnregistered(string eventName, string processDefinitionKey)
        => LogProcessUnregistered(eventName, processDefinitionKey);
    protected override void OnProcessAlreadyRegistered(string eventName, string processDefinitionKey)
        => LogProcessAlreadyRegistered(eventName, processDefinitionKey);
    protected override void OnProcessNotFound(string eventName, string processDefinitionKey)
        => LogProcessNotFound(eventName, processDefinitionKey);
    protected override void OnNoRegisteredProcesses(string eventName)
        => LogNoRegisteredProcesses(eventName);
    protected override void OnProcessDisabledSkipped(string eventName, string processDefinitionKey)
        => LogProcessDisabledSkipped(eventName, processDefinitionKey);
    protected override void OnStartEventFired(string eventName, string processDefinitionKey, Guid instanceId)
        => LogMessageStartEventFired(eventName, processDefinitionKey, instanceId);
    protected override void OnStartEventFailed(string eventName, string processDefinitionKey, Exception ex)
        => LogMessageStartEventFailed(eventName, processDefinitionKey, ex);
    protected override void OnHighProcessCount(string eventName, int processCount, int threshold)
        => LogHighProcessCount(eventName, processCount, threshold);

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

    [LoggerMessage(EventId = 9108, Level = LogLevel.Warning, Message = "Message start event '{MessageName}' has {ProcessCount} registered processes (threshold: {Threshold}) — delivering in batches")]
    private partial void LogHighProcessCount(string messageName, int processCount, int threshold);
}
