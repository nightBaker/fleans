

using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ParallelGateway(
    string ActivityId,
    [property: Id(1)] bool IsFork) : Gateway(ActivityId)
{
    internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        await base.ExecuteAsync(workflowContext, activityContext, definition);

        if (IsFork)
        {
            await activityContext.Complete();
        }
        else
        {
            if (await AllIncomingPathsCompleted(workflowContext, definition))
            {
                await activityContext.Complete();
            }
            // Otherwise stay active — base.ExecuteAsync already called Execute()
        }
    }

    internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var nextFlows = definition.SequenceFlows.Where(sf => sf.Source == this)
            .Select(flow => flow.Target)
            .ToList();

        return Task.FromResult(nextFlows);
    }

    private async Task<bool> AllIncomingPathsCompleted(IWorkflowExecutionContext workflowContext, IWorkflowDefinition workflow)
    {
        var incomingFlows = workflow.SequenceFlows.Where(sf => sf.Target == this).ToList();
        var completedActivities = await workflowContext.GetCompletedActivities();

        foreach (var incomingFlow in incomingFlows)
        {
            var sourceCompleted = false;
            foreach (var completedActivity in completedActivities)
            {
                if (await completedActivity.GetActivityId() == incomingFlow.Source.ActivityId)
                {
                    sourceCompleted = true;
                    break;
                }
            }

            if (!sourceCompleted)
                return false;
        }

        return true;
    }
}
