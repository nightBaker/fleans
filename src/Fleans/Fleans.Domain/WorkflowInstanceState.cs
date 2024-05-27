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
        var variablesId = Guid.NewGuid();
        VariableStates.Add(variablesId, new WorklfowVariablesState());
        ActiveActivities.Add(new ActivityInstance(startActivity, variablesId));        
    }

    internal void Start()
    {
        if (IsStarted)
            throw new InvalidOperationException("Workflow is already started");

        IsStarted = true;
    }
    internal void Complete()
    {
        if (!ActiveActivities.Any())
            throw new InvalidOperationException("Workflow is already completed");

        IsCompleted = true;
    }
}
