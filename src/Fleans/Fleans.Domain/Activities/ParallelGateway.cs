

using System.Runtime.CompilerServices;
using Fleans.Domain.States;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ParallelGateway : Gateway
{
    [Id(1)]
    public bool IsFork { get; init; }
    
    public ParallelGateway(string ActivityId, bool isFork) : base(ActivityId)
    {
        IsFork = isFork;
    }

    internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, IActivityInstance activityState)
    {
        await base.ExecuteAsync(workflowInstance, activityState);
        
        if (IsFork)
        {
            activityState.Complete();
        }
        else
        {
            if (await AllIncomingPathsCompleted(await workflowInstance.GetState(), await workflowInstance.GetWorkflowDefinition()))
            {
                activityState.Complete();
            }
            else
            {
                activityState.Execute();
            }                
        }

        
            
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, IActivityInstance state)
    {
        var definition = await workflowInstance.GetWorkflowDefinition();
        var nextFlows = definition.SequenceFlows.Where(sf => sf.Source == this)
            .Select(flow => flow.Target)
            .ToList();
        
        return nextFlows;
    }

    private async Task<bool> AllIncomingPathsCompleted(IWorkflowInstanceState state, IWorkflowDefinition workflow)
    {
        var incomingFlows = workflow.SequenceFlows.Where(sf => sf.Target == this).ToList();

        var completedActivities = await state.GetCompletedActivities();
        var activeActivities = await state.GetActiveActivities();

        var any = false;

        foreach (var incomingFlow in incomingFlows)
        {
            foreach (var completedActivity in completedActivities)
            {
                if (await completedActivity.GetCurrentActivity() == incomingFlow.Source)
                {
                    if(! await completedActivity.IsCompleted())
                    {
                        return false;
                    }
                }
            }

            

            foreach (var activeActivity in activeActivities)
            {
                if (await activeActivity.GetCurrentActivity() == incomingFlow.Source)
                {
                    any = true;
                }
            }
        }

        return any;

        //return incomingFlows.All(flow => completedActivities.Where(ca => ca.GetCurrentActivity() == flow.Source)
        //                                                        .All(ca => ca.IsCompleted))
        //    && incomingFlows.All(flow => activeActivities.Any(ca => ca.GetCurrentActivity == flow.Source));

    }
}
