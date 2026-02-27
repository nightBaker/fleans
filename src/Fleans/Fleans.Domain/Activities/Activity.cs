
using System.Runtime.CompilerServices;
using Fleans.Domain.Events;
using Orleans;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record Activity([property: Id(0)] string ActivityId)
{
    internal virtual bool IsJoinGateway => false;

    internal virtual async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        await activityContext.Execute();
        await activityContext.PublishEvent(new WorkflowActivityExecutedEvent(await workflowContext.GetWorkflowInstanceId(),
            definition.WorkflowId,
            await activityContext.GetActivityInstanceId(),
            ActivityId,
            GetType().Name));
        return [];
    }

    internal abstract Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition);
}
