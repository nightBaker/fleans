using Fleans.Domain.Activities;
using Fleans.Domain.Errors;

namespace Fleans.Domain;

public class WorkflowInstance
{
    public Workflow Workflow { get; }
    public WorkflowInstanceState State { get; }

    public WorkflowInstance(Workflow workflow)
    {
        Workflow = workflow;
        var startActivity = workflow.Activities.OfType<StartEvent>().First();
        State = new WorkflowInstanceState(startActivity);
    }

    internal void TransitionToNextActivity()
    {
        var newActiveActivities = new List<ActivityState>();
        var completedActivities = new List<ActivityState>();

        foreach (var activityState in State.ActiveActivities)
        {
            if (activityState.IsCompleted)
            {
                var currentActivity = activityState.CurrentActivity;
                if (currentActivity == null)
                    continue;

                var nextActivities = currentActivity.GetNextActivities(this, activityState);
                newActiveActivities.AddRange(nextActivities.Select(activity => new ActivityState(activity)));
                
                completedActivities.Add(activityState);
            }
        }

        State.ActiveActivities.RemoveAll(completedActivities.Contains);
        State.ActiveActivities.AddRange(newActiveActivities);
        State.CompletedActivities.AddRange(completedActivities);
    }

    internal void CompleteActivity(Guid activityId, Dictionary<string, object> variables)
    {
        var activityState = State.ActiveActivities.FirstOrDefault(x => x.CurrentActivity.ActivityId == activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        activityState.Complete();

        var variablesState = State.VariableStates[activityState.VariablesStateId];

        variablesState.Merge(variables);
    }

    internal void FailActivity(Guid activityId, Exception exception)
    {
        var activityState = State.ActiveActivities.FirstOrDefault(x => x.CurrentActivity.ActivityId == activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        activityState.Fail(exception);
    }

    internal void Start() => State.Start();

    internal void Complete() => State.Complete();
}
