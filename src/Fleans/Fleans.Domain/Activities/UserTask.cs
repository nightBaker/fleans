namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record UserTask(
    string ActivityId,
    [property: Id(1)] string? Assignee,
    [property: Id(2)] IReadOnlyList<string> CandidateGroups,
    [property: Id(3)] IReadOnlyList<string> CandidateUsers,
    [property: Id(4)] IReadOnlyList<string>? ExpectedOutputVariables
) : BoundarableActivity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        commands.Add(new RegisterUserTaskCommand(
            Assignee, CandidateGroups, CandidateUsers, ExpectedOutputVariables));

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
