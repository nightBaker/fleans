using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Orleans.Runtime;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ExclusiveGateway : ConditionalGateway
{
    public ExclusiveGateway(string activityId) : base(activityId)
    {
        ActivityId = activityId;
    }

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
        var currentActivity = await activityInstance.GetCurrentActivity();

        var sequences = (await workflowInstance.GetWorkflowDefinition()).SequenceFlows.OfType<ConditionalSequenceFlow>()
                                .Where(sf => sf.Source.ActivityId == currentActivity.ActivityId)
                                .ToArray();

        var state = await workflowInstance.GetState();
        await state.AddConditionSequenceStates(await activityInstance.GetActivityInstanceId(), sequences);
        return sequences;
    }

    private async Task QueueEvaluateConditionEvents(IWorkflowInstance workflowInstance, IActivityInstance activityInstance, IEnumerable<ConditionalSequenceFlow> sequences)
    {
        var definition = await workflowInstance.GetWorkflowDefinition();
        foreach (var sequence in sequences)
        {
            await activityInstance.PublishEvent(new EvaluateConditionEvent(workflowInstance.GetGrainId().GetGuidKey(),
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
        var state = await workflowInstance.GetState();
        var sequencesState = await state.GetConditionSequenceStates();
        var activitySequencesState = sequencesState[await activityInstance.GetActivityInstanceId()];

        var trueTarget = activitySequencesState
            .FirstOrDefault(x => x.Result);

        if (trueTarget is not null)
            return [trueTarget.ConditionalSequence.Target];

        var definition = await workflowInstance.GetWorkflowDefinition();
        var defaultFlow = definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .FirstOrDefault(sf => sf.Source.ActivityId == ActivityId);

        if (defaultFlow is not null)
            return [defaultFlow.Target];

        throw new InvalidOperationException(
            $"ExclusiveGateway {ActivityId}: no true condition and no default flow");
    }
}
