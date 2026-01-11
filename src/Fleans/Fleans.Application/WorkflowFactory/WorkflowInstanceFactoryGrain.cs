using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.States;
using Orleans.Concurrency;

namespace Fleans.Application.WorkflowFactory;

[Reentrant]
public class WorkflowInstanceFactoryGrain : Grain, IWorkflowInstanceFactoryGrain
{
    private readonly IGrainFactory _grainFactory;
    private readonly Dictionary<string, IWorkflowDefinition> _workflows = new();

    public WorkflowInstanceFactoryGrain(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }
    
    public async Task<IWorkflowInstance> CreateWorkflowInstanceGrain(string workflowId)
    {
        if (!_workflows.TryGetValue(workflowId, out var workflow))
        {
            throw new KeyNotFoundException($"Workflow with id '{workflowId}' is not registered. Ensure the workflow is registered before creating instances.");
        }

        var guid = Guid.NewGuid();
        var workflowInstanceGrain = _grainFactory.GetGrain<IWorkflowInstance>(guid);
        
        await workflowInstanceGrain.SetWorkflow(workflow);
        
        return workflowInstanceGrain;
    }

    public Task RegisterWorkflow(IWorkflowDefinition workflow)
    {
        if (workflow == null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (string.IsNullOrWhiteSpace(workflow.WorkflowId))
        {
            throw new ArgumentException("WorkflowId cannot be null or empty.", nameof(workflow));
        }

        if (_workflows.ContainsKey(workflow.WorkflowId))
        {
            throw new InvalidOperationException($"Workflow with id '{workflow.WorkflowId}' is already registered.");
        }

        _workflows[workflow.WorkflowId] = workflow;
        return Task.CompletedTask;
    }

    public Task<bool> IsWorkflowRegistered(string workflowId)
    {
        return Task.FromResult(_workflows.ContainsKey(workflowId));
    }

    public Task<IReadOnlyList<IWorkflowDefinition>> GetAllWorkflows()
    {
        var summaries = _workflows.Values
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<IWorkflowDefinition>>(summaries);
    }
}
