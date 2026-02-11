using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Domain;

public partial class WorkflowInstance : Grain, IWorkflowInstance
{
    public ValueTask<Guid> GetWorkflowInstanceId() => ValueTask.FromResult(this.GetPrimaryKey());
    public IWorkflowDefinition WorkflowDefinition { get; private set; } = null!;
    private WorkflowInstanceState State { get; } = new();

    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowInstance> _logger;

    public WorkflowInstance(IGrainFactory grainFactory, ILogger<WorkflowInstance> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async Task StartWorkflow()
    {
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        State.ExecutionStartedAt = DateTimeOffset.UtcNow;
        LogWorkflowStarted();
        await Start();
        await ExecuteWorkflow();
    }

    private async Task ExecuteWorkflow()
    {
        while (await State.AnyNotExecuting())
        {
            foreach (var activityState in await State.GetNotExecutingNotCompletedActivities())
            {
                var currentActivity = await activityState.GetCurrentActivity();
                SetActivityRequestContext(currentActivity.ActivityId, activityState);
                LogExecutingActivity(currentActivity.ActivityId, currentActivity.GetType().Name);
                await currentActivity.ExecuteAsync(this, activityState);
            }

            await TransitionToNextActivity();
        }
    }

    public async Task CompleteActivity(string activityId, ExpandoObject variables)
    {
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogCompletingActivity(activityId);
        await CompleteActivityState(activityId, variables);
        await ExecuteWorkflow();
    }

    public async Task FailActivity(string activityId, Exception exception)
    {
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogFailingActivity(activityId);
        await FailActivityState(activityId, exception);
        await ExecuteWorkflow();
    }

    private async Task TransitionToNextActivity()
    {
        var newActiveActivities = new List<IActivityInstance>();
        var completedActivities = new List<IActivityInstance>();

        foreach (var activityState in await State.GetActiveActivities())
        {

            if (await activityState.IsCompleted())
            {
                var currentActivity = await activityState.GetCurrentActivity();

                var nextActivities = await currentActivity.GetNextActivities(this, activityState);

                foreach(var nextActivity in nextActivities)
                {
                    var variablesId = await activityState.GetVariablesStateId();
                    RequestContext.Set("VariablesId", variablesId.ToString());
                    var activityInstance = _grainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
                    await activityInstance.SetVariablesId(variablesId);

                    await activityInstance.SetActivity(nextActivity);

                    newActiveActivities.Add(activityInstance);
                }

                completedActivities.Add(activityState);
            }
        }

        LogTransition(completedActivities.Count, newActiveActivities.Count);
        LogStateRemoveActiveActivities(completedActivities.Count);
        await State.RemoveActiveActivities(completedActivities);
        LogStateAddActiveActivities(newActiveActivities.Count);
        await State.AddActiveActivities(newActiveActivities);
        await State.AddCompletedActivities(completedActivities);
    }

    private async Task CompleteActivityState(string activityId, ExpandoObject variables)
    {
        var activityInstance = await State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        SetActivityRequestContext(activityId, activityInstance);
        await activityInstance.Complete();
        var variablesId = await activityInstance.GetVariablesStateId();
        RequestContext.Set("VariablesId", variablesId.ToString());

        LogStateMergeState(variablesId);
        await State.MergeState(variablesId, variables);
    }

    private async Task FailActivityState(string activityId, Exception exception)
    {
        var activityInstance = await State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        SetActivityRequestContext(activityId, activityInstance);
        await activityInstance.Fail(exception);
    }

    private ValueTask Start()
    {
        LogStateStarted();
        return State.Start();
    }

    public async ValueTask Complete()
    {
        await State.Complete();
        State.CompletedAt = DateTimeOffset.UtcNow;
        LogStateCompleted();
    }

    public async Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogConditionResult(conditionSequenceId, result);

        var activityInstance = await State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        var gateway = await activityInstance.GetCurrentActivity() as ConditionalGateway
            ?? throw new InvalidOperationException("Activity is not a conditional gateway");

        bool isDecisionMade;
        try
        {
            isDecisionMade = await gateway.SetConditionResult(
                this, activityInstance, conditionSequenceId, result);
        }
        catch (InvalidOperationException)
        {
            LogGatewayNoDefaultFlow(activityId);
            throw;
        }

        if (isDecisionMade)
        {
            if (result)
                LogGatewayShortCircuited(activityId, conditionSequenceId);
            else
                LogGatewayTakingDefaultFlow(activityId);

            LogGatewayAutoCompleting(activityId);
            await activityInstance.Complete();
            await ExecuteWorkflow();
        }
    }

    public async Task SetWorkflow(IWorkflowDefinition workflow)
    {
        if(WorkflowDefinition is not null) throw new ArgumentException("Workflow already set");

        WorkflowDefinition = workflow ?? throw new ArgumentNullException(nameof(workflow));
        State.CreatedAt = DateTimeOffset.UtcNow;

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogWorkflowDefinitionSet();

        var startActivity = workflow.Activities.OfType<StartEvent>().First();

        var variablesId = Guid.NewGuid();
        var activityInstance = _grainFactory.GetGrain<IActivityInstance>(variablesId);
        await activityInstance.SetActivity(startActivity);
        await activityInstance.SetVariablesId(variablesId);

        LogStateStartWith(startActivity.ActivityId);
        await State.StartWith(activityInstance, variablesId);
    }

    public ValueTask<DateTimeOffset?> GetCreatedAt() => ValueTask.FromResult(State.CreatedAt);

    public ValueTask<DateTimeOffset?> GetExecutionStartedAt() => ValueTask.FromResult(State.ExecutionStartedAt);

    public ValueTask<DateTimeOffset?> GetCompletedAt() => ValueTask.FromResult(State.CompletedAt);

    public async ValueTask<WorkflowInstanceInfo> GetInstanceInfo()
    {
        var isStarted = await State.IsStarted();
        var isCompleted = await State.IsCompleted();
        var defId = WorkflowDefinition?.ProcessDefinitionId ?? "";
        return new WorkflowInstanceInfo(
            this.GetPrimaryKey(), defId, isStarted, isCompleted,
            State.CreatedAt, State.ExecutionStartedAt, State.CompletedAt);
    }

    public async ValueTask<InstanceStateSnapshot> GetStateSnapshot()
        => await State.GetStateSnapshot();

    public ValueTask<IWorkflowDefinition> GetWorkflowDefinition() => ValueTask.FromResult(WorkflowDefinition);

    public async ValueTask<ExpandoObject> GetVariables(Guid variablesStateId)
    {
        var variablesState = await State.GetVariableStates();
        var variables = variablesState[variablesStateId].Variables;

        return variables;
    }

    // State facade methods — activities access state through these, not directly
    public ValueTask<IReadOnlyList<IActivityInstance>> GetActiveActivities()
        => State.GetActiveActivities();

    public ValueTask<IReadOnlyList<IActivityInstance>> GetCompletedActivities()
        => State.GetCompletedActivities();

    public ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates()
        => State.GetConditionSequenceStates();

    public ValueTask AddConditionSequenceStates(Guid activityInstanceId, ConditionalSequenceFlow[] sequences)
        => State.AddConditionSequenceStates(activityInstanceId, sequences);

    public ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result)
        => State.SetConditionSequenceResult(activityInstanceId, sequenceId, result);

    private void SetWorkflowRequestContext()
    {
        if (WorkflowDefinition is null) return;

        RequestContext.Set("WorkflowId", WorkflowDefinition.WorkflowId);
        RequestContext.Set("WorkflowInstanceId", this.GetPrimaryKey().ToString());
        if (WorkflowDefinition.ProcessDefinitionId is not null)
            RequestContext.Set("ProcessDefinitionId", WorkflowDefinition.ProcessDefinitionId);
    }

    private void SetActivityRequestContext(string activityId, IActivityInstance activityInstance)
    {
        RequestContext.Set("ActivityId", activityId);
        RequestContext.Set("ActivityInstanceId", activityInstance.GetPrimaryKey().ToString());
    }

    private IDisposable? BeginWorkflowScope()
    {
        if (WorkflowDefinition is null) return null;

        return _logger.BeginScope(
            "[{WorkflowId}, {ProcessDefinitionId}, {WorkflowInstanceId}]",
            WorkflowDefinition.WorkflowId, WorkflowDefinition.ProcessDefinitionId ?? "-", this.GetPrimaryKey().ToString());
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Workflow definition set")]
    private partial void LogWorkflowDefinitionSet();

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Workflow execution started")]
    private partial void LogWorkflowStarted();

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Executing activity {ActivityId} ({ActivityType})")]
    private partial void LogExecutingActivity(string activityId, string activityType);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug, Message = "Completing activity {ActivityId}")]
    private partial void LogCompletingActivity(string activityId);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning, Message = "Failing activity {ActivityId}")]
    private partial void LogFailingActivity(string activityId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Debug, Message = "Condition sequence result: {SequenceFlowId}={Result}")]
    private partial void LogConditionResult(string sequenceFlowId, bool result);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug, Message = "Transitioning: {CompletedCount} completed, {NewCount} new")]
    private partial void LogTransition(int completedCount, int newCount);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Debug, Message = "Gateway {ActivityId} decision made, auto-completing and resuming workflow")]
    private partial void LogGatewayAutoCompleting(string activityId);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Debug, Message = "Gateway {ActivityId} short-circuited: condition {ConditionSequenceFlowId} is true")]
    private partial void LogGatewayShortCircuited(string activityId, string conditionSequenceFlowId);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Debug, Message = "Gateway {ActivityId} all conditions false, taking default flow")]
    private partial void LogGatewayTakingDefaultFlow(string activityId);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Error, Message = "Gateway {ActivityId} all conditions false and no default flow — misconfigured workflow")]
    private partial void LogGatewayNoDefaultFlow(string activityId);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Workflow initialized with start activity {ActivityId}")]
    private partial void LogStateStartWith(string activityId);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Workflow started")]
    private partial void LogStateStarted();

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Workflow completed")]
    private partial void LogStateCompleted();

    [LoggerMessage(EventId = 3003, Level = LogLevel.Debug, Message = "Variables merged for state {VariablesId}")]
    private partial void LogStateMergeState(Guid variablesId);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Debug, Message = "Adding {Count} active activities")]
    private partial void LogStateAddActiveActivities(int count);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Debug, Message = "Removing {Count} completed activities")]
    private partial void LogStateRemoveActiveActivities(int count);
}
