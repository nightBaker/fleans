using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

[CorePlacement]
public partial class SignalStartEventListenerGrain :
    StartEventListenerGrainBase<SignalStartEventListenerState>, ISignalStartEventListenerGrain
{
    private readonly ILogger<SignalStartEventListenerGrain> _logger;

    public SignalStartEventListenerGrain(
        [PersistentState("state", GrainStorageNames.SignalStartEventListeners)] IPersistentState<SignalStartEventListenerState> state,
        ILogger<SignalStartEventListenerGrain> logger)
        : base(state)
    {
        _logger = logger;
    }

    protected override void OnProcessRegistered(string eventName, string processDefinitionKey)
        => LogProcessRegistered(eventName, processDefinitionKey);
    protected override void OnProcessUnregistered(string eventName, string processDefinitionKey)
        => LogProcessUnregistered(eventName, processDefinitionKey);
    protected override void OnProcessAlreadyRegistered(string eventName, string processDefinitionKey)
        => LogProcessAlreadyRegistered(eventName, processDefinitionKey);
    protected override void OnProcessNotFound(string eventName, string processDefinitionKey)
        => LogProcessNotFound(eventName, processDefinitionKey);

    [LoggerMessage(EventId = 9200, Level = LogLevel.Information, Message = "Registered process {ProcessDefinitionKey} for signal start event '{SignalName}'")]
    private partial void LogProcessRegistered(string signalName, string processDefinitionKey);

    [LoggerMessage(EventId = 9201, Level = LogLevel.Information, Message = "Unregistered process {ProcessDefinitionKey} from signal start event '{SignalName}'")]
    private partial void LogProcessUnregistered(string signalName, string processDefinitionKey);

    [LoggerMessage(EventId = 9206, Level = LogLevel.Debug, Message = "Process {ProcessDefinitionKey} already registered for signal start event '{SignalName}', skipping write")]
    private partial void LogProcessAlreadyRegistered(string signalName, string processDefinitionKey);

    [LoggerMessage(EventId = 9207, Level = LogLevel.Debug, Message = "Process {ProcessDefinitionKey} not found for signal start event '{SignalName}', skipping unregister")]
    private partial void LogProcessNotFound(string signalName, string processDefinitionKey);
}
