using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Fleans.Application.Services;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain, IBoundaryEventStateAccessor
{
    public ValueTask<Guid> GetWorkflowInstanceId() => ValueTask.FromResult(this.GetPrimaryKey());
    private IWorkflowDefinition? _workflowDefinition;

    private readonly IPersistentState<WorkflowInstanceState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowInstance> _logger;
    private readonly IBoundaryEventHandler _boundaryHandler;

    private WorkflowInstanceState State => _state.State;

    // IBoundaryEventStateAccessor
    WorkflowInstanceState IBoundaryEventStateAccessor.State => State;
    IGrainFactory IBoundaryEventStateAccessor.GrainFactory => _grainFactory;
    ILogger IBoundaryEventStateAccessor.Logger => _logger;
    IWorkflowExecutionContext IBoundaryEventStateAccessor.WorkflowExecutionContext => this;

    async ValueTask<object?> IBoundaryEventStateAccessor.GetVariable(Guid variablesId, string variableName) => await GetVariable(variablesId, variableName);
    async Task IBoundaryEventStateAccessor.TransitionToNextActivity() => await TransitionToNextActivity();
    async Task IBoundaryEventStateAccessor.ExecuteWorkflow() => await ExecuteWorkflow();
    async Task IBoundaryEventStateAccessor.CancelScopeChildren(Guid scopeId) => await CancelScopeChildren(scopeId);
    async Task IBoundaryEventStateAccessor.ProcessCommands(IReadOnlyList<IExecutionCommand> commands, ActivityInstanceEntry entry, IActivityExecutionContext activityContext) => await ProcessCommands(commands, entry, activityContext);

    public WorkflowInstance(
        [PersistentState("state", GrainStorageNames.WorkflowInstances)] IPersistentState<WorkflowInstanceState> state,
        IGrainFactory grainFactory,
        ILogger<WorkflowInstance> logger,
        IBoundaryEventHandler boundaryHandler)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
        _boundaryHandler = boundaryHandler;
        _boundaryHandler.Initialize(this);
    }

    public async Task SetWorkflow(IWorkflowDefinition workflow)
    {
        if(_workflowDefinition is not null) throw new ArgumentException("Workflow already set");

        _workflowDefinition = workflow ?? throw new ArgumentNullException(nameof(workflow));

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogWorkflowDefinitionSet();

        var startActivity = workflow.Activities.FirstOrDefault(a => a is StartEvent or TimerStartEvent)
            ?? throw new InvalidOperationException("Workflow must have a StartEvent or TimerStartEvent");

        var activityInstanceId = Guid.NewGuid();
        var variablesId = Guid.NewGuid();
        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(activityInstanceId);
        await activityInstance.SetActivity(startActivity.ActivityId, startActivity.GetType().Name);
        await activityInstance.SetVariablesId(variablesId);

        var entry = new ActivityInstanceEntry(activityInstanceId, startActivity.ActivityId, this.GetPrimaryKey());
        LogStateStartWith(startActivity.ActivityId);
        State.StartWith(this.GetPrimaryKey(), workflow.ProcessDefinitionId, entry, variablesId);
        await _state.WriteStateAsync();
    }

    private async ValueTask<IWorkflowDefinition> GetWorkflowDefinition()
    {
        await EnsureWorkflowDefinitionAsync();
        return _workflowDefinition!;
    }

    private async ValueTask EnsureWorkflowDefinitionAsync()
    {
        if (_workflowDefinition is not null)
            return;

        var processDefId = State.ProcessDefinitionId
            ?? throw new InvalidOperationException("ProcessDefinitionId not set — call SetWorkflow first.");

        var grain = _grainFactory.GetGrain<IProcessDefinitionGrain>(processDefId);
        _workflowDefinition = await grain.GetDefinition();
    }

    private void SetWorkflowRequestContext()
    {
        if (_workflowDefinition is null) return;

        RequestContext.Set("WorkflowId", _workflowDefinition.WorkflowId);
        RequestContext.Set("WorkflowInstanceId", this.GetPrimaryKey().ToString());
        if (_workflowDefinition.ProcessDefinitionId is not null)
            RequestContext.Set("ProcessDefinitionId", _workflowDefinition.ProcessDefinitionId);
    }

    private void SetActivityRequestContext(string activityId, IActivityInstanceGrain activityInstance)
    {
        RequestContext.Set("ActivityId", activityId);
        RequestContext.Set("ActivityInstanceId", activityInstance.GetPrimaryKey().ToString());
    }

    private IDisposable? BeginWorkflowScope()
    {
        if (_workflowDefinition is null) return null;

        return _logger.BeginScope(
            "[{WorkflowId}, {ProcessDefinitionId}, {WorkflowInstanceId}]",
            _workflowDefinition.WorkflowId, _workflowDefinition.ProcessDefinitionId ?? "-", this.GetPrimaryKey().ToString());
    }
}
