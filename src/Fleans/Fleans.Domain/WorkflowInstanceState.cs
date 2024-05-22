using Fleans.Domain.Activities;

namespace Fleans.Domain;

public class WorkflowInstanceState
{
    public bool IsStarted { get; private set; }
    public bool IsCompleted { get; private set; }

    public List<ActivityInstance> ActiveActivities { get; } = new List<ActivityInstance>();
    public List<ActivityInstance> CompletedActivities { get; } = new List<ActivityInstance>();

    public Dictionary<Guid, WorklfowVariablesState> VariableStates { get; } = new();

    public WorkflowInstanceState(Activity startActivity)
    {
        ActiveActivities.Add(new ActivityInstance(startActivity));
    }

    internal void Start() => IsStarted = true;
    internal void Complete() => IsCompleted = true;
}
