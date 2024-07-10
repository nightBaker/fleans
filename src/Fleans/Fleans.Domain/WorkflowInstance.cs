using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Orleans;
using Orleans.Runtime;
using System.Dynamic;
using System.Security.Cryptography;

namespace Fleans.Domain;

public class WorkflowInstance : Grain, IWorkflowInstance
{
    public ValueTask<Guid> GetWorkflowInstanceId() => ValueTask.FromResult(this.GetPrimaryKey());
    public IWorkflowDefinition WorkflowDefinition { get; private set; } = null!;
    public IWorkflowInstanceState State { get; private set; } = null!; 
    
    private readonly IGrainFactory _grainFactory;    

    public WorkflowInstance(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    private readonly Queue<IDomainEvent> _events = new();

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        
        State = _grainFactory.GetGrain<IWorkflowInstanceState>(this.GetPrimaryKey());

        return base.OnActivateAsync(cancellationToken);
    }

    public async Task StartWorkflow()
    {
        await ExecuteWorkflow();
    }

    private async Task ExecuteWorkflow()
    {
        while (await State.AnyNotExecuting())
        {
            foreach (var activityState in await State.GetNotExecutingNotCompletedActivities())
            {
                var currentActivity = await activityState.GetCurrentActivity();
                await currentActivity.ExecuteAsync(this, activityState);
            }

            await TransitionToNextActivity();            
        }
    }
    
    public async Task CompleteActivity(string activityId, ExpandoObject variables)
    {
        await CompleteActivityState(activityId, variables);
        await ExecuteWorkflow();
    }

    public async Task FailActivity(string activityId, Exception exception)
    {
        await FailActivityState(activityId, exception);
        await ExecuteWorkflow();
    }

    private async Task TransitionToNextActivity()
    {
        var newActiveActivities = new List<IActivityInstance>();
        var completedActivities = new List<IActivityInstance>();

        foreach (var activityState in await State.GetActiveActivities())
        {
            if (await activityState.IsCompleted())
            {
                var currentActivity = await activityState.GetCurrentActivity();
                if (currentActivity == null)
                    continue;

                var nextActivities = await currentActivity.GetNextActivities(this, activityState);

                foreach(var nextActivity in nextActivities)
                {
                    var variablesId = await activityState.GetVariablesStateId();
                    var activityInstance = _grainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
                    activityInstance.SetVariablesId(variablesId);

                    activityInstance.SetActivity(nextActivity);

                    newActiveActivities.Add(activityInstance);
                }
                
                completedActivities.Add(activityState);
            }
        }

        State.RemoveActiveActivities(completedActivities);
        State.AddActiveActivities(newActiveActivities);
        State.AddCompletedActivities(completedActivities);
    }

    private async Task CompleteActivityState(string activityId, ExpandoObject variables)
    {
        var activityInstance = await State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        activityInstance.Complete();
        var variablesId = await activityInstance.GetVariablesStateId();

        State.MergeState(variablesId, variables);        
    }

    private async Task FailActivityState(string activityId, Exception exception)
    {
        var activityInstance = await State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        activityInstance.Fail(exception);
    }

    public void Start() 
        => State.Start();

    public void Complete() 
        => State.Complete();

    public async Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        var activityInstance = await State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        await (await activityInstance.GetCurrentActivity() as Gateway ?? throw new Exception("Acitivity is not gateway type"))
                .SetConditionResult(this, activityInstance, conditionSequenceId, result);
    }

    public void EnqueueEvent(IDomainEvent domainEvent)
    {
        _events.Enqueue(domainEvent);
    }

    public Task SetWorkflow(IWorkflowDefinition workflow)
    {
        if(WorkflowDefinition is not null) throw new ArgumentException("Workflow already set");

        WorkflowDefinition = workflow ?? throw new ArgumentNullException(nameof(workflow));
        State = _grainFactory.GetGrain<IWorkflowInstanceState>(this.GetPrimaryKey());

        var startActivity = workflow.Activities.OfType<StartEvent>().First();
        State.StartWith(startActivity);

        return Task.CompletedTask;
    }

    public ValueTask<IWorkflowInstanceState> GetState() => ValueTask.FromResult(State);
    
    public ValueTask<IWorkflowDefinition> GetWorkflowDefinition() => ValueTask.FromResult(WorkflowDefinition);

    public async ValueTask<ExpandoObject> GetVariables(Guid variablesStateId)
    {
        var variablesState = await State.GetVariableStates();
        var variables = variablesState[variablesStateId].Variables;

        return variables;
    }
}
