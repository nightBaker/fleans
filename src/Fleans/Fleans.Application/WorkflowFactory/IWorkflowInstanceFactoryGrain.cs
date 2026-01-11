using Fleans.Domain;

namespace Fleans.Application.WorkflowFactory;

public interface IWorkflowInstanceFactoryGrain : IGrainWithIntegerKey
{
    Task<IWorkflowInstance> CreateWorkflowInstanceGrain(string workflowId);
    Task RegisterWorkflow(IWorkflowDefinition workflow);
    Task<bool> IsWorkflowRegistered(string workflowId);
    Task<IReadOnlyList<WorkflowSummary>> GetAllWorkflows();
}

public record WorkflowSummary(string WorkflowId, int ActivitiesCount, int SequenceFlowsCount);
