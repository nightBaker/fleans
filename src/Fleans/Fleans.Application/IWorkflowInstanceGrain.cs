using Fleans.Domain;

namespace Fleans.Application;

public interface IWorkflowInstanceGrain : IGrainWithGuidKey
{    
    void StartWorkflow();
    void CompleteActivity(string activityId, Dictionary<string, object> variables);
    void SetWorkflow(Workflow workflow);
}
