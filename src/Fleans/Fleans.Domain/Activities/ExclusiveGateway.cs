using Fleans.Domain.Events;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ExclusiveGateway(string ActivityId) : ConditionalGateway(ActivityId)
{
    internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
    {
        await base.ExecuteAsync(workflowInstance, activityInstance);

        var sequences = await AddConditionalSequencesToWorkflowInstance(workflowInstance, activityInstance);

        if (!sequences.Any())
        {
            await activityInstance.Complete();
            return;
        }

        await QueueEvaluateConditionEvents(workflowInstance, activityInstance, sequences);
    }

    private static async Task<IEnumerable<ConditionalSequenceFlow>> AddConditionalSequencesToWorkflowInstance(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
    {
        var activityId = await activityInstance.GetActivityId();

        var sequences = (await workflowInstance.GetWorkflowDefinition()).SequenceFlows.OfType<ConditionalSequenceFlow>()
                                .Where(sf => sf.Source.ActivityId == activityId)
                                .ToArray();

        var sequenceFlowIds = sequences.Select(s => s.SequenceFlowId).ToArray();
        await workflowInstance.AddConditionSequenceStates(await activityInstance.GetActivityInstanceId(), sequenceFlowIds);
        return sequences;
    }

    private async Task QueueEvaluateConditionEvents(IWorkflowInstance workflowInstance, IActivityInstance activityInstance, IEnumerable<ConditionalSequenceFlow> sequences)
    {
        var definition = await workflowInstance.GetWorkflowDefinition();
        foreach (var sequence in sequences)
        {
            await activityInstance.PublishEvent(new EvaluateConditionEvent(await workflowInstance.GetWorkflowInstanceId(),
                                                               definition.WorkflowId,
                                                                definition.ProcessDefinitionId,
                                                                await activityInstance.GetActivityInstanceId(),
                                                                ActivityId,
                                                                sequence.SequenceFlowId,
                                                                sequence.Condition));
        }
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
    {
        var sequencesState = await workflowInstance.GetConditionSequenceStates();
        var activityInstanceId = await activityInstance.GetActivityInstanceId();
        if (!sequencesState.TryGetValue(activityInstanceId, out var activitySequencesState))
            activitySequencesState = [];
        var definition = await workflowInstance.GetWorkflowDefinition();

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
