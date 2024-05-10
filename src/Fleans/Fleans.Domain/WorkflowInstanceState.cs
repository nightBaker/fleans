using Fleans.Domain.Activities;

namespace Fleans.Domain;

public class WorkflowInstanceState
{
    public bool IsStarted { get; private set; }
    public bool IsCompleted { get; private set; }

    public List<ActivityState> ActiveActivities { get; } = new List<ActivityState>();
    public List<ActivityState> CompletedActivities { get; } = new List<ActivityState>();

    public Dictionary<Guid, WorklfowVariablesState> VariableStates { get; } = new();

    public WorkflowInstanceState(Activity startActivity)
    {
        ActiveActivities.Add(new ActivityState(startActivity));
    }

    internal void Start() => IsStarted = true;
    internal void Complete() => IsCompleted = true;
}
