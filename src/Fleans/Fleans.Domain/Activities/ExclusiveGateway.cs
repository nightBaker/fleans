using Fleans.Domain.Events;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ExclusiveGateway(string ActivityId) : ConditionalGateway(ActivityId)
{
    internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        await base.ExecuteAsync(workflowContext, activityContext, definition);

        var sequences = await AddConditionalSequencesToWorkflowInstance(workflowContext, activityContext, definition);

        if (!sequences.Any())
        {
            await activityContext.Complete();
            return;
        }

        await QueueEvaluateConditionEvents(workflowContext, activityContext, sequences, definition);
    }

    private static async Task<IEnumerable<ConditionalSequenceFlow>> AddConditionalSequencesToWorkflowInstance(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var activityId = await activityContext.GetActivityId();

        var sequences = definition.SequenceFlows.OfType<ConditionalSequenceFlow>()
                                .Where(sf => sf.Source.ActivityId == activityId)
                                .ToArray();

        var sequenceFlowIds = sequences.Select(s => s.SequenceFlowId).ToArray();
        await workflowContext.AddConditionSequenceStates(await activityContext.GetActivityInstanceId(), sequenceFlowIds);
        return sequences;
    }

    private async Task QueueEvaluateConditionEvents(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IEnumerable<ConditionalSequenceFlow> sequences, IWorkflowDefinition definition)
    {
        foreach (var sequence in sequences)
        {
            await activityContext.PublishEvent(new EvaluateConditionEvent(await workflowContext.GetWorkflowInstanceId(),
                                                               definition.WorkflowId,
                                                                definition.ProcessDefinitionId,
                                                                await activityContext.GetActivityInstanceId(),
                                                                ActivityId,
                                                                sequence.SequenceFlowId,
                                                                sequence.Condition));
        }
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var sequencesState = await workflowContext.GetConditionSequenceStates();
        var activityInstanceId = await activityContext.GetActivityInstanceId();
        if (!sequencesState.TryGetValue(activityInstanceId, out var activitySequencesState))
            activitySequencesState = [];

        var trueTarget = activitySequencesState
            .FirstOrDefault(x => x.Result);

        if (trueTarget is not null)
        {
            var flow = definition.SequenceFlows
                .FirstOrDefault(sf => sf.SequenceFlowId == trueTarget.ConditionalSequenceFlowId)
                ?? throw new InvalidOperationException(
                    $"Sequence flow '{trueTarget.ConditionalSequenceFlowId}' not found in workflow definition");
            return [flow.Target];
        }

        var defaultFlow = definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .FirstOrDefault(sf => sf.Source.ActivityId == ActivityId);

        if (defaultFlow is not null)
            return [defaultFlow.Target];

        throw new InvalidOperationException(
            $"ExclusiveGateway {ActivityId}: no true condition and no default flow");
    }
}
