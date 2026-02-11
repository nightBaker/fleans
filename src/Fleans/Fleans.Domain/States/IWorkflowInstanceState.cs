
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Domain.States
{
    public interface IWorkflowInstanceState
    {
        DateTimeOffset? CreatedAt { get; }
        DateTimeOffset? ExecutionStartedAt { get; }
        DateTimeOffset? CompletedAt { get; }

        ValueTask<IReadOnlyList<IActivityInstance>> GetActiveActivities();
        ValueTask<IReadOnlyList<IActivityInstance>> GetCompletedActivities();
        ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
        ValueTask<bool> IsCompleted();
        ValueTask<bool> IsStarted();
        ValueTask<IReadOnlyDictionary<Guid, WorklfowVariablesState>> GetVariableStates();
        ValueTask<IActivityInstance?> GetFirstActive(string activityId);
        ValueTask<bool> AnyNotExecuting();
        ValueTask<IActivityInstance[]> GetNotExecutingNotCompletedActivities();
        ValueTask<InstanceStateSnapshot> GetStateSnapshot();

        ValueTask<Guid> AddCloneOfVariableState(Guid variableStateId);
        ValueTask AddActiveActivities(IEnumerable<IActivityInstance> activities);
        ValueTask AddCompletedActivities(IEnumerable<IActivityInstance> activities);
        ValueTask AddConditionSequenceStates(Guid activityInstanceId, ConditionalSequenceFlow[] sequences);
        ValueTask Complete();
        ValueTask RemoveActiveActivities(List<IActivityInstance> removeInstances);
        ValueTask Start();
        ValueTask StartWith(IActivityInstance activityInstance, Guid variablesId);
        ValueTask SetCondigitionSequencesResult(Guid activityInstanceId, string sequenceId, bool result);
        ValueTask MergeState(Guid variablesId, ExpandoObject variables);
    }
}
