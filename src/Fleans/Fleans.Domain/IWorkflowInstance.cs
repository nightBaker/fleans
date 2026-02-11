using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
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
    ValueTask<DateTimeOffset?> GetCreatedAt();
    [ReadOnly]
    ValueTask<DateTimeOffset?> GetExecutionStartedAt();
    [ReadOnly]
    ValueTask<DateTimeOffset?> GetCompletedAt();
    [ReadOnly]
    ValueTask<WorkflowInstanceInfo> GetInstanceInfo();
    [ReadOnly]
    ValueTask<InstanceStateSnapshot> GetStateSnapshot();
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
    ValueTask AddConditionSequenceStates(Guid activityInstanceId, ConditionalSequenceFlow[] sequences);
    ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result);
}
