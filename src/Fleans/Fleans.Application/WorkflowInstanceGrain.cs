using Fleans.Domain;

namespace Fleans.Application;

public class WorkflowInstanceGrain : Grain, IWorkflowInstanceGrain
{
    private  WorkflowInstance? _workflowInstance;

    private WorkflowInstance WorkflowInstance 
        => _workflowInstance ?? throw new InvalidOperationException("Workflow not set");

    public void SetWorkflow(Workflow workflow)
    {
        _workflowInstance = new WorkflowInstance(workflow);
    }

    public void StartWorkflow()
    {
        WorkflowInstance.StartWorkflow();            
    }

    public void CompleteActivity(string activityId, Dictionary<string, object> variables)
    {
        WorkflowInstance.CompleteActivity(activityId, variables);
    }
}
