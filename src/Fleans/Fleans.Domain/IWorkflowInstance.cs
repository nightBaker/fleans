using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Orleans;
using Orleans.Concurrency;
using System.Dynamic;

namespace Fleans.Domain;

public interface IWorkflowInstance : IGrainWithGuidKey
{
    [ReadOnly]
    ValueTask<Guid> GetWorkflowInstanceId();
    [ReadOnly]
    ValueTask<IWorkflowDefinition> GetWorkflowDefinition();

    Task CompleteActivity(string activityId, ExpandoObject variables);
    Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result);
    Task FailActivity(string activityId, Exception exception);
    Task StartWorkflow();
    Task SetWorkflow(IWorkflowDefinition workflow);
    ValueTask Complete();

    [ReadOnly]
    ValueTask<ExpandoObject> GetVariables(Guid variablesStateId);

    // State facade methods for activities
    [ReadOnly]
    ValueTask<IReadOnlyList<IActivityInstance>> GetActiveActivities();
    [ReadOnly]
    ValueTask<IReadOnlyList<IActivityInstance>> GetCompletedActivities();
    [ReadOnly]
    ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
    ValueTask AddConditionSequenceStates(Guid activityInstanceId, string[] sequenceFlowIds);
    ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result);
}
