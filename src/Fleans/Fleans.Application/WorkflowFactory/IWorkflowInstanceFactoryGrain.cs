using Fleans.Domain;

namespace Fleans.Application.WorkflowFactory;

public interface IWorkflowInstanceFactoryGrain : IGrainWithIntegerKey
{
    Task<IWorkflowInstance> CreateWorkflowInstanceGrain(string workflowId);
}
