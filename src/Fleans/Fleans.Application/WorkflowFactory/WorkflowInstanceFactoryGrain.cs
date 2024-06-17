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
    
    public Task<IWorkflowInstance> CreateWorkflowInstanceGrain(string workflowId)
    {
        var workflow = _workflows[workflowId] ?? throw new Exception("Workflow not found");
        var guid = Guid.NewGuid();
        var workflowInstanceGrain = _grainFactory.GetGrain<IWorkflowInstance>(guid);
        var workflowInstanceStateGrain = _grainFactory.GetGrain<IWorkflowInstanceState>(guid);
        workflowInstanceGrain.SetWorkflow(workflow);
        return Task.FromResult(workflowInstanceGrain);
    }
}
