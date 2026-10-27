
using System.Runtime.CompilerServices;
using Fleans.Domain.Events;
using Orleans;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record Activity([property: Id(0)] string ActivityId)
{
    /// <summary>
    /// <c>fleans:input</c> mappings: evaluated against the enclosing scope before the activity
    /// executes, into an activity-local scope the activity sees and the process does not.
    /// </summary>
    [Id(1)]
    public List<InputMapping> InputMappings { get; init; } = new();

    /// <summary>
    /// <c>fleans:output</c> mappings: evaluated on completion against the activity's local
    /// variables and its result. When any are defined, only they reach the enclosing scope.
    /// </summary>
    [Id(2)]
    public List<OutputMapping> OutputMappings { get; init; } = new();

    internal virtual bool IsJoinGateway => false;

    internal virtual async Task<List<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        await activityContext.Execute();
        await activityContext.PublishEvent(new WorkflowActivityExecutedEvent(await workflowContext.GetWorkflowInstanceId(),
            definition.WorkflowId,
            await activityContext.GetActivityInstanceId(),
            ActivityId,
            GetType().Name));
        return [];
    }

    internal abstract Task<List<ActivityTransition>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition);

    // Mappings compare by content, not list reference, so a definition read back from storage
    // still equals itself (sequence flows find their source and target by equality).
    public virtual bool Equals(Activity? other) =>
        other is not null
        && EqualityContract == other.EqualityContract
        && ActivityId == other.ActivityId
        && InputMappings.SequenceEqual(other.InputMappings)
        && OutputMappings.SequenceEqual(other.OutputMappings);

    public override int GetHashCode() => HashCode.Combine(EqualityContract, ActivityId);
}
