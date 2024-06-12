
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans;

namespace Fleans.Domain.States
{
    public interface IWorkflowInstanceState : IGrainWithGuidKey
    {        
        ValueTask<IReadOnlyList<ActivityInstance>> GetActiveActivities();
        ValueTask<IReadOnlyList<ActivityInstance>> GetCompletedActivities();
        ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
        ValueTask<bool> IsCompleted();
        ValueTask<bool> IsStarted();
        ValueTask<IReadOnlyDictionary<Guid, WorklfowVariablesState>> GetVariableStates();

        ValueTask<Guid> AddCloneOfVariableState(Guid variableStateId);
        void AddActiveActivities(IEnumerable<ActivityInstance> activities);        
        void AddCompletedActivities(IEnumerable<ActivityInstance> activities);
        void AddConditionSequenceStates(Guid activityInstanceId, IEnumerable<ConditionalSequenceFlow> sequences);
        void Complete();
        void RemoveActiveActivities(List<ActivityInstance> removeInstances);
        void Start();
        void StartWith(Activity startActivity);
    }
}