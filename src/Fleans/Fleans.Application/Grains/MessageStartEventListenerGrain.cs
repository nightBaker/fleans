using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

[CorePlacement]
public partial class MessageStartEventListenerGrain :
    StartEventListenerGrainBase<MessageStartEventListenerState>, IMessageStartEventListenerGrain
{
    private readonly ILogger<MessageStartEventListenerGrain> _logger;

    public MessageStartEventListenerGrain(
        [PersistentState("state", GrainStorageNames.MessageStartEventListeners)] IPersistentState<MessageStartEventListenerState> state,
        ILogger<MessageStartEventListenerGrain> logger)
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

    [LoggerMessage(EventId = 9100, Level = LogLevel.Information, Message = "Registered process {ProcessDefinitionKey} for message start event '{MessageName}'")]
    private partial void LogProcessRegistered(string messageName, string processDefinitionKey);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Information, Message = "Unregistered process {ProcessDefinitionKey} from message start event '{MessageName}'")]
    private partial void LogProcessUnregistered(string messageName, string processDefinitionKey);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Debug, Message = "Process {ProcessDefinitionKey} already registered for message start event '{MessageName}', skipping write")]
    private partial void LogProcessAlreadyRegistered(string messageName, string processDefinitionKey);

    [LoggerMessage(EventId = 9106, Level = LogLevel.Debug, Message = "Process {ProcessDefinitionKey} not found for message start event '{MessageName}', skipping unregister")]
    private partial void LogProcessNotFound(string messageName, string processDefinitionKey);
}
