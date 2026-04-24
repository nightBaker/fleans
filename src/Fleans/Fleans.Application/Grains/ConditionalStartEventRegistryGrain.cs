using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

[CorePlacement]
public partial class ConditionalStartEventRegistryGrain : Grain, IConditionalStartEventRegistryGrain
{
    private readonly IPersistentState<ConditionalStartEventRegistryState> _state;
    private readonly ILogger<ConditionalStartEventRegistryGrain> _logger;

    public ConditionalStartEventRegistryGrain(
        [PersistentState("state", GrainStorageNames.ConditionalStartEventRegistry)] IPersistentState<ConditionalStartEventRegistryState> state,
        ILogger<ConditionalStartEventRegistryGrain> logger)
    {
        _state = state;
        _logger = logger;
    }

    public async ValueTask Register(string processDefinitionKey, string activityId, string conditionExpression)
    {
        if (_state.State.Add(processDefinitionKey, activityId, conditionExpression))
        {
            await _state.WriteStateAsync();
            LogRegistered(processDefinitionKey, activityId);
        }
    }

    public async ValueTask Unregister(string processDefinitionKey, string activityId)
    {
        if (_state.State.Remove(processDefinitionKey, activityId))
        {
            await _state.WriteStateAsync();
            LogUnregistered(processDefinitionKey, activityId);
        }
    }

    public async ValueTask UnregisterAllForProcess(string processDefinitionKey)
    {
        var removed = _state.State.RemoveAllForProcess(processDefinitionKey);
        if (removed > 0)
        {
            await _state.WriteStateAsync();
            LogUnregisteredAll(processDefinitionKey, removed);
        }
    }

    [ReadOnly]
    public ValueTask<List<ConditionalStartEntry>> GetAll()
    {
        var entries = _state.State.Entries
            .Select(e => new ConditionalStartEntry(e.ProcessDefinitionKey, e.ActivityId, e.ConditionExpression))
            .ToList();
        return ValueTask.FromResult(entries);
    }

    [ReadOnly]
    public ValueTask<List<ConditionalStartEntry>> GetByProcess(string processDefinitionKey)
    {
        var entries = _state.State.Entries
            .Where(e => e.ProcessDefinitionKey == processDefinitionKey)
            .Select(e => new ConditionalStartEntry(e.ProcessDefinitionKey, e.ActivityId, e.ConditionExpression))
            .ToList();
        return ValueTask.FromResult(entries);
    }

    [LoggerMessage(EventId = 9310, Level = LogLevel.Information,
        Message = "Registered conditional start event in registry: process {ProcessDefinitionKey}, activity {ActivityId}")]
    private partial void LogRegistered(string processDefinitionKey, string activityId);

    [LoggerMessage(EventId = 9311, Level = LogLevel.Information,
        Message = "Unregistered conditional start event from registry: process {ProcessDefinitionKey}, activity {ActivityId}")]
    private partial void LogUnregistered(string processDefinitionKey, string activityId);

    [LoggerMessage(EventId = 9312, Level = LogLevel.Information,
        Message = "Unregistered all {Count} conditional start events for process {ProcessDefinitionKey}")]
    private partial void LogUnregisteredAll(string processDefinitionKey, int count);
}
