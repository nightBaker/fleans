namespace Fleans.Application;

public interface IWorkflowInstanceFactoryGrain : IGrainWithIntegerKey
{    
    Task<IWorkflowInstanceGrain> CreateWorkflowInstance(string workflowId);
}
