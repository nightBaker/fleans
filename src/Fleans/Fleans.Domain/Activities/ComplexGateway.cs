using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ComplexGateway(
    string ActivityId,
    bool IsFork,
    string? ActivationCondition
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
            // Fork: mirror InclusiveGateway fork path — evaluate all conditional outgoing flows
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
        else if (ActivationCondition is null)
        {
            // Join without condition: mirror ParallelGateway — wait for all tokens
            if (await AllExpectedTokensArrived(workflowContext, definition))
                await activityContext.Complete();
        }
        else
        {
            // Join with activation condition
            var activityInstanceId = await activityContext.GetActivityInstanceId();

            // Already-fired guard: if another token fired the gateway, discard this late arrival
            var existingState = await workflowContext.GetComplexGatewayJoinState(activityInstanceId);
            if (existingState?.HasFired == true)
                return commands; // GetNextActivities handles RestoreParent token cleanup

            // Create join state (if needed) and increment token count via event sourcing
            await workflowContext.IncrementComplexGatewayJoinToken(activityInstanceId, ActivationCondition);
            var joinState = (await workflowContext.GetComplexGatewayJoinState(activityInstanceId))!;

            commands.Add(new EvaluateActivationConditionCommand(ActivationCondition, joinState.WaitingTokenCount));
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
        // Fork condition evaluation — mirror InclusiveGateway.SetConditionResult
        var activityInstanceId = await activityContext.GetActivityInstanceId();
        await workflowContext.SetConditionSequenceResult(activityInstanceId, conditionSequenceFlowId, result);

        var sequences = await workflowContext.GetConditionSequenceStates();
        if (!sequences.TryGetValue(activityInstanceId, out var mySequences))
            return false;

        if (!mySequences.All(s => s.IsEvaluated))
            return false;

        if (mySequences.Any(s => s.Result))
            return true;

        var hasDefault = definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .Any(sf => sf.Source.ActivityId == ActivityId);

        if (!hasDefault)
            throw new InvalidOperationException(
                $"ComplexGateway {ActivityId}: all conditions false and no default flow");

        return true;
    }

    internal override async Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        if (!IsFork)
        {
            // Join: return single outgoing flow, restore parent token
            return definition.GetOutgoingFlows(this)
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
            .Select(x => definition.GetSequenceFlow(x.ConditionalSequenceFlowId).Target)
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
            $"ComplexGateway {ActivityId}: no true condition and no default flow");
    }
}
