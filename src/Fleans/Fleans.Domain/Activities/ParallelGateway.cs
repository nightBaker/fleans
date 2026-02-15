

using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ParallelGateway(
    string ActivityId,
    [property: Id(1)] bool IsFork) : Gateway(ActivityId)
{
    internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        await base.ExecuteAsync(workflowContext, activityContext);

        if (IsFork)
        {
            await activityContext.Complete();
        }
        else
        {
            if (await AllIncomingPathsCompleted(workflowContext, await workflowContext.GetWorkflowDefinition()))
            {
                await activityContext.Complete();
            }
            else
            {
                await activityContext.Execute();
            }
        }
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var nextFlows = definition.SequenceFlows.Where(sf => sf.Source == this)
            .Select(flow => flow.Target)
            .ToList();

        return nextFlows;
    }

    private async Task<bool> AllIncomingPathsCompleted(IWorkflowExecutionContext workflowContext, IWorkflowDefinition workflow)
    {
        var incomingFlows = workflow.SequenceFlows.Where(sf => sf.Target == this).ToList();

        var completedActivities = await workflowContext.GetCompletedActivities();
        var activeActivities = await workflowContext.GetActiveActivities();

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
