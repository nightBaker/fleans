using Fleans.Domain;

namespace Fleans.Application.WorkflowGrains;

public interface IWorkflowInstanceGrain : IGrainWithGuidKey
{
    void StartWorkflow();
    void CompleteActivity(string activityId, Dictionary<string, object> variables);
    void SetWorkflow(Workflow workflow);
}
