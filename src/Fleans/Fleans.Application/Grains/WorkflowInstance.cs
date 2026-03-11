using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain
{
    public ValueTask<Guid> GetWorkflowInstanceId() => ValueTask.FromResult(this.GetPrimaryKey());
    private IWorkflowDefinition? _workflowDefinition;
    private WorkflowExecution? _execution;

    private readonly IPersistentState<WorkflowInstanceState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowInstance> _logger;

    private WorkflowInstanceState State => _state.State;

    public WorkflowInstance(
        [PersistentState("state", GrainStorageNames.WorkflowInstances)] IPersistentState<WorkflowInstanceState> state,
        IGrainFactory grainFactory,
        ILogger<WorkflowInstance> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    private async ValueTask EnsureExecution()
    {
        await EnsureWorkflowDefinitionAsync();
        _execution ??= new WorkflowExecution(State, _workflowDefinition!);
    }

    public async Task SetWorkflow(IWorkflowDefinition workflow, string? startActivityId = null)
    {
        if (_workflowDefinition is not null) throw new ArgumentException("Workflow already set");

        _workflowDefinition = workflow ?? throw new ArgumentNullException(nameof(workflow));

        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogWorkflowDefinitionSet();

        _execution = new WorkflowExecution(State, workflow);
        _execution.Start(startActivityId);
        LogAndClearEvents();
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

    private IDisposable? BeginWorkflowScope()
    {
        if (_workflowDefinition is null) return null;

        return _logger.BeginScope(
            "[{WorkflowId}, {ProcessDefinitionId}, {WorkflowInstanceId}]",
            _workflowDefinition.WorkflowId, _workflowDefinition.ProcessDefinitionId ?? "-", this.GetPrimaryKey().ToString());
    }
}
