using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record InclusiveGateway(
    string ActivityId,
    bool IsFork
) : ForkJoinGateway(ActivityId, IsFork)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        if (IsFork)
        {
            // Fork: collect conditional flows and emit AddConditionsCommand
            var activityId = await activityContext.GetActivityId();
            var sequences = definition.SequenceFlows.OfType<ConditionalSequenceFlow>()
                .Where(sf => sf.Source.ActivityId == activityId)
                .ToArray();

            if (sequences.Length == 0)
            {
                await activityContext.Complete();
                return commands;
            }

            var sequenceFlowIds = sequences.Select(s => s.SequenceFlowId).ToArray();
            var evaluations = sequences.Select(s => new ConditionEvaluation(s.SequenceFlowId, s.Condition)).ToList();
            commands.Add(new AddConditionsCommand(sequenceFlowIds, evaluations));
        }
        else
        {
            // Join: check if all expected tokens have arrived
            if (await AllExpectedTokensArrived(workflowContext, definition))
                await activityContext.Complete();
        }

        return commands;
    }

    internal override async Task<bool> SetConditionResult(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        string conditionSequenceFlowId,
        bool result,
        IWorkflowDefinition definition)
    {
        var activityInstanceId = await activityContext.GetActivityInstanceId();
        await workflowContext.SetConditionSequenceResult(activityInstanceId, conditionSequenceFlowId, result);

        var sequences = await workflowContext.GetConditionSequenceStates();
        if (!sequences.TryGetValue(activityInstanceId, out var mySequences))
            return false;

        // Wait for ALL conditions — never short-circuit on first true
        if (!mySequences.All(s => s.IsEvaluated))
            return false;

        if (mySequences.Any(s => s.Result))
            return true;

        // All false — need default flow
        var hasDefault = definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .Any(sf => sf.Source.ActivityId == ActivityId);

        if (!hasDefault)
            throw new InvalidOperationException(
                $"InclusiveGateway {ActivityId}: all conditions false and no default flow");

        return true;
    }

    internal override async Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        if (!IsFork)
        {
            // Join: return all outgoing flows, restore parent token
            return definition.SequenceFlows.Where(sf => sf.Source == this)
                .Select(flow => new ActivityTransition(flow.Target, Token: TokenAction.RestoreParent))
                .ToList();
        }

        // Fork: return all flows where condition was true, clone variables + create new tokens
        var sequencesState = await workflowContext.GetConditionSequenceStates();
        var activityInstanceId = await activityContext.GetActivityInstanceId();
        if (!sequencesState.TryGetValue(activityInstanceId, out var activitySequencesState))
            activitySequencesState = [];

        var trueTargets = activitySequencesState
            .Where(x => x.Result)
            .Select(x => definition.SequenceFlows
                .First(sf => sf.SequenceFlowId == x.ConditionalSequenceFlowId).Target)
            .Select(target => new ActivityTransition(target, CloneVariables: true, Token: TokenAction.CreateNew))
            .ToList();

        if (trueTargets.Count > 0)
            return trueTargets;

        var defaultFlow = definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .FirstOrDefault(sf => sf.Source.ActivityId == ActivityId);

        if (defaultFlow is not null)
            return [new ActivityTransition(defaultFlow.Target, CloneVariables: true, Token: TokenAction.CreateNew)];

        throw new InvalidOperationException(
            $"InclusiveGateway {ActivityId}: no true condition and no default flow");
    }
}
