namespace Fleans.Domain.Activities;

/// <summary>
/// Compensation Intermediate Throw Event — triggers compensation for already-completed activities.
/// If <see cref="TargetActivityRef"/> is set, only the named activity is compensated (targeted).
/// If null, all compensable completed activities in the current scope are compensated (broadcast).
/// After the compensation walk completes, execution continues via the outgoing sequence flow.
/// </summary>
[GenerateSerializer]
public record CompensationIntermediateThrowEvent(
    string ActivityId,
    [property: Id(1)] string? TargetActivityRef) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        var throwerInstanceId = await activityContext.GetActivityInstanceId();
        commands.Add(new CompensationRequestedCommand(throwerInstanceId, TargetActivityRef));
        await activityContext.Complete();
        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlow = definition.GetOutgoingFlow(this);
        return Task.FromResult(nextFlow != null
            ? new List<ActivityTransition> { new(nextFlow.Target) }
            : new List<ActivityTransition>());
    }
}
