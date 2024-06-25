
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans;

namespace Fleans.Domain.States
{
    public interface IWorkflowInstanceState : IGrainWithGuidKey
    {        
        ValueTask<IReadOnlyList<IActivityInstance>> GetActiveActivities();
        ValueTask<IReadOnlyList<IActivityInstance>> GetCompletedActivities();
        ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
        ValueTask<bool> IsCompleted();
        ValueTask<bool> IsStarted();
        ValueTask<IReadOnlyDictionary<Guid, WorklfowVariablesState>> GetVariableStates();

        ValueTask<Guid> AddCloneOfVariableState(Guid variableStateId);
        void AddActiveActivities(IEnumerable<IActivityInstance> activities);        
        void AddCompletedActivities(IEnumerable<IActivityInstance> activities);
        void AddConditionSequenceStates(Guid activityInstanceId, ConditionalSequenceFlow[] sequences);
        void Complete();
        void RemoveActiveActivities(List<IActivityInstance> removeInstances);
        void Start();
        void StartWith(Activity startActivity);
        ValueTask<IActivityInstance?> GetFirstActive(string activityId);
        ValueTask<bool> AnyNotExecuting();
        ValueTask<IActivityInstance[]> GetNotExecutingNotCompletedActivities();
        void SetCondigitionSequencesResult(Guid activityInstanceId, string sequenceId, bool result);
    }
}