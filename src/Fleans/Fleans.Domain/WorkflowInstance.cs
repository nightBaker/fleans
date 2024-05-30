using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using System.Security.Cryptography;

namespace Fleans.Domain;

public class WorkflowInstance
{
    public Guid WorkflowInstanceId { get; }
    public Workflow Workflow { get; }
    public WorkflowInstanceState State { get; }

    private readonly Queue<IDomainEvent> _events = new();

    public WorkflowInstance(Guid workflowInstanceId, Workflow workflow)
    {
        WorkflowInstanceId = workflowInstanceId;
        Workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        var startActivity = workflow.Activities.OfType<StartEvent>().First();
        State = new WorkflowInstanceState(startActivity);        
    }

    public void StartWorkflow(IEventPublisher eventPublisher)
    {        
        ExecuteWorkflow(eventPublisher);
    }

    private void ExecuteWorkflow(IEventPublisher eventPublisher)
    {
        while (State.ActiveActivities.Any(x => !x.IsExecuting))
        {
            foreach (var activityState in State.ActiveActivities.Where(x => !x.IsExecuting && !x.IsCompleted))
            {
                activityState.CurrentActivity.Execute(this, activityState);
            }

            TransitionToNextActivity();

            PublishEvents(eventPublisher);
        }
    }

    private void PublishEvents(IEventPublisher eventPublisher)
    {
        while (_events.Count > 0)
        {
            var domainEvent = _events.Dequeue();
            eventPublisher.Publish(domainEvent);
        }
    }

    public void CompleteActivity(string activityId, Dictionary<string, object> variables, IEventPublisher eventPublisher)
    {
        CompleteActivityState(activityId, variables);
        ExecuteWorkflow(eventPublisher);
    }

    public void FailActivity(string activityId, Exception exception, IEventPublisher eventPublisher)
    {
        FailActivityState(activityId, exception);
        ExecuteWorkflow(eventPublisher);
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

    public void CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        var activityInstance = State.ActiveActivities.FirstOrDefault(x => x.CurrentActivity.ActivityId == activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        (activityInstance.CurrentActivity as Gateway ?? throw new Exception("Acitivi is not gateway type"))
                .SetConditionResult(this, activityInstance, conditionSequenceId, result);        
    }

    internal void EnqueueEvent(IDomainEvent domainEvent)
    {
        _events.Enqueue(domainEvent);
    }
}
