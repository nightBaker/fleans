using Fleans.Domain;

namespace Fleans.Application;

public class WorkflowInstanceFactoryGrain : Grain, IWorkflowInstanceFactoryGrain
{
    private readonly IGrainFactory _grainFactory;
    private readonly Dictionary<string, Workflow> _workflows;

    public WorkflowInstanceFactoryGrain(IGrainFactory grainFactory, Dictionary<string, Workflow> workflows)
    {
        _grainFactory = grainFactory;
        _workflows = workflows;
    }

    public Task<IWorkflowInstanceGrain> CreateWorkflowInstance(string workflowId)
    {        
        var workflow = _workflows[workflowId] ?? throw new Exception("Workflow not found");
        var workflowInstanceGrain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        workflowInstanceGrain.SetWorkflow(workflow);
        return Task.FromResult(workflowInstanceGrain);
    }
}
