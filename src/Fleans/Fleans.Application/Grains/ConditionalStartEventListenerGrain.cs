using System.Dynamic;
using Fleans.Application.Conditions;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class ConditionalStartEventListenerGrain : Grain, IConditionalStartEventListenerGrain
{
    private readonly IPersistentState<ConditionalStartEventListenerState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<ConditionalStartEventListenerGrain> _logger;

    public ConditionalStartEventListenerGrain(
        [PersistentState("state", GrainStorageNames.ConditionalStartEventListeners)] IPersistentState<ConditionalStartEventListenerState> state,
        IGrainFactory grainFactory,
        ILogger<ConditionalStartEventListenerGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
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

    public async ValueTask<Guid?> EvaluateAndStart(ExpandoObject variables)
    {
        if (!_state.State.IsRegistered)
        {
            LogNotRegistered(this.GetPrimaryKeyString());
            return null;
        }

        var processKey = _state.State.ProcessDefinitionKey;
        var activityId = _state.State.ActivityId;
        var expression = _state.State.ConditionExpression;

        var evaluator = _grainFactory.GetGrain<IConditionExpressionEvaluatorGrain>(0);
        var result = await evaluator.Evaluate(expression, variables);

        if (!result)
        {
            LogConditionFalse(processKey, activityId);
            return null;
        }

        var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(processKey);
        var isActive = await processGrain.IsActive();
        if (!isActive)
        {
            LogProcessDisabledSkipped(processKey, activityId);
            return null;
        }

        var definition = await processGrain.GetLatestDefinition();
        var startActivityId = FindConditionalStartActivityId(definition, activityId);
        if (startActivityId == null)
        {
            LogStartActivityNotFound(processKey, activityId);
            return null;
        }

        var instanceId = Guid.NewGuid();
        var instance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        await instance.SetWorkflow(definition, startActivityId);
        await instance.SetInitialVariables(variables);
        await instance.StartWorkflow();

        LogConditionalStartEventFired(processKey, activityId, instanceId);
        return instanceId;
    }

    private static string? FindConditionalStartActivityId(IWorkflowDefinition definition, string activityId)
    {
        var activity = definition.FindActivity(activityId);
        return activity is ConditionalStartEvent ? activityId : null;
    }

    [LoggerMessage(EventId = 9300, Level = LogLevel.Information,
        Message = "Registered conditional start event listener for process {ProcessDefinitionKey}, activity {ActivityId}, expression '{ConditionExpression}'")]
    private partial void LogRegistered(string processDefinitionKey, string activityId, string conditionExpression);

    [LoggerMessage(EventId = 9301, Level = LogLevel.Information,
        Message = "Unregistered conditional start event listener for process {ProcessDefinitionKey}, activity {ActivityId}")]
    private partial void LogUnregistered(string processDefinitionKey, string activityId);

    [LoggerMessage(EventId = 9302, Level = LogLevel.Information,
        Message = "Conditional start event fired for process {ProcessDefinitionKey}, activity {ActivityId}, created instance {InstanceId}")]
    private partial void LogConditionalStartEventFired(string processDefinitionKey, string activityId, Guid instanceId);

    [LoggerMessage(EventId = 9303, Level = LogLevel.Debug,
        Message = "Condition evaluated false for process {ProcessDefinitionKey}, activity {ActivityId}")]
    private partial void LogConditionFalse(string processDefinitionKey, string activityId);

    [LoggerMessage(EventId = 9304, Level = LogLevel.Warning,
        Message = "Skipping disabled process {ProcessDefinitionKey} for conditional start event activity {ActivityId}")]
    private partial void LogProcessDisabledSkipped(string processDefinitionKey, string activityId);

    [LoggerMessage(EventId = 9305, Level = LogLevel.Warning,
        Message = "Conditional start activity {ActivityId} not found in latest definition for process {ProcessDefinitionKey}")]
    private partial void LogStartActivityNotFound(string processDefinitionKey, string activityId);

    [LoggerMessage(EventId = 9307, Level = LogLevel.Debug,
        Message = "Conditional start event listener grain {GrainKey} is not registered, skipping evaluation")]
    private partial void LogNotRegistered(string grainKey);
}
