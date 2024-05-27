using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;

namespace Fleans.Domain;

public class WorkflowInstance
{
    public Workflow Workflow { get; }
    public WorkflowInstanceState State { get; }

    internal Queue<IDomainEvent> Events { get; } = new();

    public WorkflowInstance(Workflow workflow)
    {
        Workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        var startActivity = workflow.Activities.OfType<StartEvent>().First();
        State = new WorkflowInstanceState(startActivity);
    }

    public void StartWorkflow()
    {        
        ExecuteWorkflow();
    }

    private void ExecuteWorkflow()
    {
        while (State.ActiveActivities.Any(x => !x.IsExecuting))
        {
            foreach (var activityState in State.ActiveActivities.Where(x => !x.IsExecuting && !x.IsCompleted))
            {
                activityState.CurrentActivity.Execute(this, activityState);
            }

            TransitionToNextActivity();
        }
    }

    public void CompleteActivity(string activityId, Dictionary<string, object> variables)
    {
        CompleteActivityState(activityId, variables);
        ExecuteWorkflow();
    }

    public void FailActivity(string activityId, Exception exception)
    {
        FailActivityState(activityId, exception);
        ExecuteWorkflow();
    }

    private void TransitionToNextActivity()
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
                newActiveActivities.AddRange(nextActivities.Select(activity => new ActivityInstance(activity, activityState.VariablesStateId)));
                
                completedActivities.Add(activityState);
            }
        }

        State.ActiveActivities.RemoveAll(completedActivities.Contains);
        State.ActiveActivities.AddRange(newActiveActivities);
        State.CompletedActivities.AddRange(completedActivities);
    }

    private void CompleteActivityState(string activityId, Dictionary<string, object> variables)
    {
        var activityInstance = State.ActiveActivities.FirstOrDefault(x => x.CurrentActivity.ActivityId == activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        activityInstance.Complete();

        var variablesState = State.VariableStates[activityInstance.VariablesStateId];

        variablesState.Merge(variables);
    }

    private void FailActivityState(string activityId, Exception exception)
    {
        var activityInstance = State.ActiveActivities.FirstOrDefault(x => x.CurrentActivity.ActivityId == activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        activityInstance.Fail(exception);
    }

    internal void Start() => State.Start();

    internal void Complete() => State.Complete();
}
