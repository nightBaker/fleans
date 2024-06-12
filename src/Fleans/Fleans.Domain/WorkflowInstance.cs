using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Orleans;
using Orleans.Runtime;
using System.Security.Cryptography;

namespace Fleans.Domain;

public class WorkflowInstance : Grain, IWorkflowInstance
{
    public ValueTask<Guid> GetWorkflowInstanceId() => ValueTask.FromResult(this.GetGrainId().GetGuidKey());
    public IWorkflowDefinition WorkflowDefinition { get; private set; } = null!;
    public IWorkflowInstanceState State { get; private set; } = null!;    

    private readonly Queue<IDomainEvent> _events = new();
    
    public async Task StartWorkflow(IEventPublisher eventPublisher)
    {
        await ExecuteWorkflow(eventPublisher);
    }

    private async Task ExecuteWorkflow(IEventPublisher eventPublisher)
    {
        while ((await State.GetActiveActivities()).Any(x => !x.IsExecuting))
        {
            foreach (var activityState in (await State.GetActiveActivities()).Where(x => !x.IsExecuting && !x.IsCompleted))
            {
                activityState.CurrentActivity.ExecuteAsync(this, activityState);
            }

            await TransitionToNextActivity();

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

    public async Task CompleteActivity(string activityId, Dictionary<string, object> variables, IEventPublisher eventPublisher)
    {
        await CompleteActivityState(activityId, variables);
        await ExecuteWorkflow(eventPublisher);
    }

    public async Task FailActivity(string activityId, Exception exception, IEventPublisher eventPublisher)
    {
        await FailActivityState(activityId, exception);
        await ExecuteWorkflow(eventPublisher);
    }

    private async Task TransitionToNextActivity()
    {
        var newActiveActivities = new List<ActivityInstance>();
        var completedActivities = new List<ActivityInstance>();

        foreach (var activityState in await State.GetActiveActivities())
        {
            if (activityState.IsCompleted)
            {
                var currentActivity = activityState.CurrentActivity;
                if (currentActivity == null)
                    continue;

                var nextActivities = await currentActivity.GetNextActivities(this, activityState);
                newActiveActivities.AddRange(nextActivities.Select(activity => new ActivityInstance(activity, activityState.VariablesStateId)));

                completedActivities.Add(activityState);
            }
        }

        State.RemoveActiveActivities(completedActivities);
        State.AddActiveActivities(newActiveActivities);
        State.AddCompletedActivities(completedActivities);
    }

    private async Task CompleteActivityState(string activityId, Dictionary<string, object> variables)
    {
        var activityInstance = (await State.GetActiveActivities()).FirstOrDefault(x => x.CurrentActivity.ActivityId == activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        activityInstance.Complete();

        var variablesState = (await State.GetVariableStates())[activityInstance.VariablesStateId];

        variablesState.Merge(variables);
    }

    private async Task FailActivityState(string activityId, Exception exception)
    {
        var activityInstance = (await State.GetActiveActivities()).FirstOrDefault(x => x.CurrentActivity.ActivityId == activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        activityInstance.Fail(exception);
    }

    public void Start() => State.Start();

    public void Complete() => State.Complete();

    public async Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        var activityInstance = (await State.GetActiveActivities()).FirstOrDefault(x => x.CurrentActivity.ActivityId == activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        await (activityInstance.CurrentActivity as Gateway ?? throw new Exception("Acitivi is not gateway type"))
                .SetConditionResult(this, activityInstance, conditionSequenceId, result);
    }

    public void EnqueueEvent(IDomainEvent domainEvent)
    {
        _events.Enqueue(domainEvent);
    }

    public Task SetWorkflow(IWorkflowDefinition workflow, IWorkflowInstanceState workflowInstanceState)
    {
        if(WorkflowDefinition is not null) throw new ArgumentException("Workflow already set");

        WorkflowDefinition = workflow ?? throw new ArgumentNullException(nameof(workflow));
        State = workflowInstanceState ?? throw new ArgumentNullException(nameof(workflowInstanceState));

        var startActivity = workflow.Activities.OfType<StartEvent>().First();
        workflowInstanceState.StartWith(startActivity);

        return Task.CompletedTask;
    }

    public ValueTask<IWorkflowInstanceState> GetState() => ValueTask.FromResult(State);
    
    public ValueTask<IWorkflowDefinition> GetWorkflowDefinition() => ValueTask.FromResult(WorkflowDefinition);
}
