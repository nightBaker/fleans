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
        var newActiveActivities = new List<ActivityInstance>();
        var completedActivities = new List<ActivityInstance>();

        foreach (var activityState in State.ActiveActivities)
        {
            if (activityState.IsCompleted)
            {
                var currentActivity = activityState.CurrentActivity;
                if (currentActivity == null)
                    continue;

                var nextActivities = currentActivity.GetNextActivities(this, activityState);
                newActiveActivities.AddRange(nextActivities.Select(activity => new ActivityInstance(activity)));
                
                completedActivities.Add(activityState);
            }
        }

        State.ActiveActivities.RemoveAll(completedActivities.Contains);
        State.ActiveActivities.AddRange(newActiveActivities);
        State.CompletedActivities.AddRange(completedActivities);
    }

    internal void CompleteActivity(string activityId, Dictionary<string, object> variables)
    {
        var activityState = State.ActiveActivities.FirstOrDefault(x => x.CurrentActivity.ActivityId == activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        activityState.Complete();

        var variablesState = State.VariableStates[activityState.VariablesStateId];

        variablesState.Merge(variables);
    }

    internal void FailActivity(string activityId, Exception exception)
    {
        var activityInstance = State.ActiveActivities.FirstOrDefault(x => x.CurrentActivity.ActivityId == activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        activityInstance.Fail(exception);
    }

    internal void Start() => State.Start();

    internal void Complete() => State.Complete();
}
