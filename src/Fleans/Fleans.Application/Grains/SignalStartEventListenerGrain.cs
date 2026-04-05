using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class SignalStartEventListenerGrain :
    StartEventListenerGrainBase<SignalStartEventListenerState>, ISignalStartEventListenerGrain
{
    private readonly ILogger<SignalStartEventListenerGrain> _logger;

    public SignalStartEventListenerGrain(
        [PersistentState("state", GrainStorageNames.SignalStartEventListeners)] IPersistentState<SignalStartEventListenerState> state,
        IGrainFactory grainFactory,
        ILogger<SignalStartEventListenerGrain> logger)
        : base(state, grainFactory)
    {
        _logger = logger;
    }

    public async ValueTask<List<Guid>> FireSignalStartEvent()
        => await FireStartEventCore(null);

    protected override string? FindStartActivityId(IWorkflowDefinition definition, string eventName)
    {
        foreach (var activity in definition.Activities.OfType<SignalStartEvent>())
        {
            var sigDef = definition.FindSignalDefinition(activity.SignalDefinitionId);
            if (sigDef?.Name == eventName)
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
        => LogSignalStartEventFired(eventName, processDefinitionKey, instanceId);
    protected override void OnStartEventFailed(string eventName, string processDefinitionKey, Exception ex)
        => LogSignalStartEventFailed(eventName, processDefinitionKey, ex);
    protected override void OnHighProcessCount(string eventName, int processCount, int threshold)
        => LogHighProcessCount(eventName, processCount, threshold);

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

    [LoggerMessage(EventId = 9205, Level = LogLevel.Warning, Message = "Skipping disabled process {ProcessDefinitionKey} for signal '{SignalName}'")]
    private partial void LogProcessDisabledSkipped(string signalName, string processDefinitionKey);

    [LoggerMessage(EventId = 9206, Level = LogLevel.Debug, Message = "Process {ProcessDefinitionKey} already registered for signal start event '{SignalName}', skipping write")]
    private partial void LogProcessAlreadyRegistered(string signalName, string processDefinitionKey);

    [LoggerMessage(EventId = 9207, Level = LogLevel.Debug, Message = "Process {ProcessDefinitionKey} not found for signal start event '{SignalName}', skipping unregister")]
    private partial void LogProcessNotFound(string signalName, string processDefinitionKey);

    [LoggerMessage(EventId = 9208, Level = LogLevel.Warning, Message = "Signal start event '{SignalName}' has {ProcessCount} registered processes (threshold: {Threshold}) — delivering in batches")]
    private partial void LogHighProcessCount(string signalName, int processCount, int threshold);
}
