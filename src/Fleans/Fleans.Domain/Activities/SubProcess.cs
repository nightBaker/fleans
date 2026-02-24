using Fleans.Domain.Sequences;
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record SubProcess(string ActivityId) : BoundarableActivity(ActivityId), IWorkflowDefinition
{
    [Id(1)]
    public List<Activity> Activities { get; init; } = [];

    [Id(2)]
    public List<SequenceFlow> SequenceFlows { get; init; } = [];

    public string WorkflowId => ActivityId;
    public string? ProcessDefinitionId => null;
    public bool IsRootScope => false;

    [Id(3)]
    public List<MessageDefinition> Messages { get; init; } = [];

    [Id(4)]
    public List<SignalDefinition> Signals { get; init; } = [];

    public Activity GetActivity(string activityId)
        => Activities.First(a => a.ActivityId == activityId);

    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        // Publish executed event but do NOT call Complete — sub-process waits for children.
        await activityContext.Execute();
        await activityContext.PublishEvent(new Events.WorkflowActivityExecutedEvent(
            await workflowContext.GetWorkflowInstanceId(),
            definition.WorkflowId,
            await activityContext.GetActivityInstanceId(),
            ActivityId,
            GetType().Name));
    }

    internal override Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlows = definition.SequenceFlows
            .Where(sf => sf.Source == this)
            .Select(flow => flow.Target)
            .ToList();
        return Task.FromResult(nextFlows);
    }
}
