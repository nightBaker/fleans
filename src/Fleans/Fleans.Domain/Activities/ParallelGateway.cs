

using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public class ParallelGateway : Gateway
{
    [Id(1)]
    public bool IsFork { get; set; }

    public ParallelGateway(string ActivityId, bool isFork) : base(ActivityId)
    {
        IsFork = isFork;
    }

    internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, IActivityInstance activityState)
    {
        await base.ExecuteAsync(workflowInstance, activityState);

        if (IsFork)
        {
            await activityState.Complete();
        }
        else
        {
            if (await AllIncomingPathsCompleted(workflowInstance, await workflowInstance.GetWorkflowDefinition()))
            {
                await activityState.Complete();
            }
            else
            {
                await activityState.Execute();
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

    private async Task<bool> AllIncomingPathsCompleted(IWorkflowInstance workflowInstance, IWorkflowDefinition workflow)
    {
        var incomingFlows = workflow.SequenceFlows.Where(sf => sf.Target == this).ToList();

        var completedActivities = await workflowInstance.GetCompletedActivities();
        var activeActivities = await workflowInstance.GetActiveActivities();

        var any = false;

        foreach (var incomingFlow in incomingFlows)
        {
            foreach (var completedActivity in completedActivities)
            {
                if (await completedActivity.GetActivityId() == incomingFlow.Source.ActivityId)
                {
                    if(! await completedActivity.IsCompleted())
                    {
                        return false;
                    }
                }
            }

            foreach (var activeActivity in activeActivities)
            {
                if (await activeActivity.GetActivityId() == incomingFlow.Source.ActivityId)
                {
                    any = true;
                }
            }
        }

        return any;
    }
}
