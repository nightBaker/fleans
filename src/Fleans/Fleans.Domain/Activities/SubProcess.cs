using Fleans.Domain.Events;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities;

/// <summary>
/// An embedded sub-process that executes a nested set of activities within the same
/// workflow instance. Implements <see cref="IWorkflowDefinition"/> so that existing
/// activity execution code can resolve child activities without scope-specific hacks.
/// </summary>
[GenerateSerializer]
public record SubProcess(string ActivityId) : BoundarableActivity(ActivityId), IWorkflowDefinition
{
    [Id(1)]
    public List<Activity> Activities { get; init; } = [];

    [Id(2)]
    public List<SequenceFlow> SequenceFlows { get; init; } = [];

    // IWorkflowDefinition — re-use existing execution infrastructure unchanged
    string IWorkflowDefinition.WorkflowId => ActivityId;
    string? IWorkflowDefinition.ProcessDefinitionId => null;
    List<Activity> IWorkflowDefinition.Activities => Activities;
    List<SequenceFlow> IWorkflowDefinition.SequenceFlows => SequenceFlows;
    List<MessageDefinition> IWorkflowDefinition.Messages => [];
    List<SignalDefinition> IWorkflowDefinition.Signals => [];
    Activity IWorkflowDefinition.GetActivity(string activityId)
        => Activities.First(a => a.ActivityId == activityId);

    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition parentDefinition)
    {
        await activityContext.Execute();
        await activityContext.PublishEvent(new WorkflowActivityExecutedEvent(
            await workflowContext.GetWorkflowInstanceId(),
            parentDefinition.WorkflowId,
            await activityContext.GetActivityInstanceId(),
            ActivityId,
            nameof(SubProcess)));

        // Open a nested scope in the workflow context; the sub-process activity instance
        // does NOT complete here — it completes when the scope has no remaining children.
        var instanceId = await activityContext.GetActivityInstanceId();
        var variablesId = await activityContext.GetVariablesStateId();
        await workflowContext.OpenSubProcessScope(instanceId, this, variablesId);
    }

    internal override Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        // Outgoing flows of the sub-process are in the parent (containing) definition
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return Task.FromResult(nextFlow != null
            ? new List<Activity> { nextFlow.Target }
            : new List<Activity>());
    }
}
