namespace Fleans.Application.WorkflowGrains;

public interface IWorkflowInstanceFactoryGrain : IGrainWithIntegerKey
{
    Task<IWorkflowInstanceGrain> CreateWorkflowInstanceGrain(string workflowId);
}
