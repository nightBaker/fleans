using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities;

/// <summary>
/// BPMN Event Sub-Process — a sub-process embedded within a parent scope that is
/// triggered by an event (message, signal, timer, or error) rather than by sequence
/// flow. Distinct from <see cref="SubProcess"/>: it has no incoming sequence flows
/// and cannot have boundary events attached. <see cref="IsInterrupting"/> controls
/// whether the parent scope's other activities are cancelled when the trigger fires.
///
/// Slice #A (this PR): domain type + parser only. Execution semantics are added
/// in subsequent slices (see issue #227).
/// </summary>
[GenerateSerializer]
public record EventSubProcess(string ActivityId) : Activity(ActivityId), IWorkflowDefinition
{
    [Id(1)]
    public List<Activity> Activities { get; init; } = [];

    [Id(2)]
    public List<SequenceFlow> SequenceFlows { get; init; } = [];

    [Id(3)]
    public bool IsInterrupting { get; init; } = true;

    public string WorkflowId => ActivityId;
    public string? ProcessDefinitionId => null;
    public bool IsRootScope => false;

    [Id(4)]
    public List<MessageDefinition> Messages { get; init; } = [];

    [Id(5)]
    public List<SignalDefinition> Signals { get; init; } = [];

    public Activity GetActivity(string activityId)
        => Activities.First(a => a.ActivityId == activityId);

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        // Event sub-processes have no incoming sequence flows and are activated
        // only by their trigger event. They never participate in normal flow
        // routing, so they have no "next" activities from a flow perspective.
        // Slice #A: stub returning empty. Slices #B–#F wire up event-triggered
        // activation via commands/effects in WorkflowExecution.
        return Task.FromResult(new List<ActivityTransition>());
    }
}
