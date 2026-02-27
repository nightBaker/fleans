

using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record EndEvent(string ActivityId) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        commands.Add(new CompleteCommand());

        // Only complete the workflow if this is a top-level EndEvent
        if (definition.IsRootScope)
            commands.Add(new CompleteWorkflowCommand());

        return commands;
    }

    internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        return Task.FromResult(new List<Activity>());
    }
}
