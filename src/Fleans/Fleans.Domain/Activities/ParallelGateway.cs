using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ParallelGateway(
    string ActivityId,
    [property: Id(1)] bool IsFork) : ForkJoinGateway(ActivityId, IsFork)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        if (IsFork)
        {
            await activityContext.Complete();
        }
        else
        {
            if (await AllExpectedTokensArrived(workflowContext, definition))
            {
                await activityContext.Complete();
            }
        }

        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        if (!IsFork)
        {
            // Join: restore parent token
            var joinFlows = definition.GetOutgoingFlows(this)
                .Select(flow => new ActivityTransition(flow.Target, Token: TokenAction.RestoreParent))
                .ToList();
            return Task.FromResult(joinFlows);
        }

        // Fork: all outgoing paths with cloned variables and new tokens
        var nextFlows = definition.GetOutgoingFlows(this)
            .Select(flow => new ActivityTransition(flow.Target, CloneVariables: true, Token: TokenAction.CreateNew))
            .ToList();

        return Task.FromResult(nextFlows);
    }
}
