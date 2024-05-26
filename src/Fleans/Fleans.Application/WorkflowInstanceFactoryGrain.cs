using Fleans.Domain;

namespace Fleans.Application;

public class WorkflowInstanceFactoryGrain : Grain, IWorkflowInstanceFactoryGrain
{
    private readonly IGrainFactory _grainFactory;
    private readonly Dictionary<string, Workflow> _workflows = new();

    public WorkflowInstanceFactoryGrain(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    public Task<IWorkflowInstanceGrain> CreateWorkflowInstance(string workflowId)
    {        
        var workflow = _workflows[workflowId] ?? throw new Exception("Workflow not found");
        var workflowInstanceGrain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        workflowInstanceGrain.SetWorkflow(workflow);
        return Task.FromResult(workflowInstanceGrain);
    }
}
