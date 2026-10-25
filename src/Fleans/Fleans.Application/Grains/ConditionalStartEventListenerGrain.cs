using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

[CorePlacement]
public partial class ConditionalStartEventListenerGrain : Grain, IConditionalStartEventListenerGrain
{
    private readonly IPersistentState<ConditionalStartEventListenerState> _state;
    private readonly ILogger<ConditionalStartEventListenerGrain> _logger;

    public ConditionalStartEventListenerGrain(
        [PersistentState("state", GrainStorageNames.ConditionalStartEventListeners)] IPersistentState<ConditionalStartEventListenerState> state,
        ILogger<ConditionalStartEventListenerGrain> logger)
    {
        _state = state;
        _logger = logger;
    }

    public async ValueTask Register(string processDefinitionKey, string activityId, string conditionExpression)
    {
        _state.State.Register(this.GetPrimaryKeyString(), processDefinitionKey, activityId, conditionExpression);
        await _state.WriteStateAsync();
        LogRegistered(processDefinitionKey, activityId, conditionExpression);
    }

    public async ValueTask Unregister()
    {
        if (!_state.State.IsRegistered) return;

        var processKey = _state.State.ProcessDefinitionKey;
        var activityId = _state.State.ActivityId;
        await _state.ClearStateAsync();
        LogUnregistered(processKey, activityId);
    }

    [LoggerMessage(EventId = 9300, Level = LogLevel.Information,
        Message = "Registered conditional start event listener for process {ProcessDefinitionKey}, activity {ActivityId}, expression '{ConditionExpression}'")]
    private partial void LogRegistered(string processDefinitionKey, string activityId, string conditionExpression);

    [LoggerMessage(EventId = 9301, Level = LogLevel.Information,
        Message = "Unregistered conditional start event listener for process {ProcessDefinitionKey}, activity {ActivityId}")]
    private partial void LogUnregistered(string processDefinitionKey, string activityId);
}
