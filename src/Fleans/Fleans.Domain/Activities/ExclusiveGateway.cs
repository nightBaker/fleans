using Fleans.Domain.Events;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ExclusiveGateway(string ActivityId) : ConditionalGateway(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        var activityId = await activityContext.GetActivityId();
        var sequences = definition.SequenceFlows.OfType<ConditionalSequenceFlow>()
            .Where(sf => sf.Source.ActivityId == activityId)
            .ToArray();

        if (sequences.Length == 0)
        {
            commands.Add(new CompleteCommand());
            return commands;
        }

        var sequenceFlowIds = sequences.Select(s => s.SequenceFlowId).ToArray();
        var evaluations = sequences.Select(s => new ConditionEvaluation(s.SequenceFlowId, s.Condition)).ToList();
        commands.Add(new AddConditionsCommand(sequenceFlowIds, evaluations));

        return commands;
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
