
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans;
using Orleans.Concurrency;
using System.Dynamic;

namespace Fleans.Domain.States
{
    public interface IWorkflowInstanceState : IGrainWithGuidKey
    {
        [ReadOnly]
        ValueTask<IReadOnlyList<IActivityInstance>> GetActiveActivities();
        [ReadOnly]
        ValueTask<IReadOnlyList<IActivityInstance>> GetCompletedActivities();
        [ReadOnly]
        ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
        [ReadOnly]
        ValueTask<bool> IsCompleted();
        [ReadOnly]
        ValueTask<bool> IsStarted();
        [ReadOnly]
        ValueTask<IReadOnlyDictionary<Guid, WorklfowVariablesState>> GetVariableStates();
        [ReadOnly]
        ValueTask<IActivityInstance?> GetFirstActive(string activityId);
        [ReadOnly]
        ValueTask<bool> AnyNotExecuting();
        [ReadOnly]
        ValueTask<IActivityInstance[]> GetNotExecutingNotCompletedActivities();
        [ReadOnly]
        ValueTask<InstanceStateSnapshot> GetStateSnapshot();

        ValueTask<Guid> AddCloneOfVariableState(Guid variableStateId);
        ValueTask AddActiveActivities(IEnumerable<IActivityInstance> activities);
        ValueTask AddCompletedActivities(IEnumerable<IActivityInstance> activities);
        ValueTask AddConditionSequenceStates(Guid activityInstanceId, ConditionalSequenceFlow[] sequences);
        ValueTask Complete();
        ValueTask RemoveActiveActivities(List<IActivityInstance> removeInstances);
        ValueTask Start();
        ValueTask StartWith(Activity startActivity);
        ValueTask SetCondigitionSequencesResult(Guid activityInstanceId, string sequenceId, bool result);
        ValueTask MergeState(Guid variablesId, ExpandoObject variables);
    }
}
