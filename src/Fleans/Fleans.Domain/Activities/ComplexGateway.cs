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
            if (await AllExpectedTokensArrived(workflowContext, definition, activityContext))
                await activityContext.Complete();
        }
        else
        {
            // Join with activation condition — state is per gateway and scope instance (#839),
            // shared by every token arriving at this join in that scope
            var activityId = await activityContext.GetActivityId();
            var activityInstanceId = await activityContext.GetActivityInstanceId();

            // Already-fired guard: if another token fired the gateway, discard this late arrival
            var existingState = await workflowContext.GetComplexGatewayJoinState(activityInstanceId);
            if (existingState?.HasFired == true)
            {
                commands.Add(new DiscardLateTokenCommand(
                    $"ComplexGateway '{activityId}' already fired — discarding late token"));
                return commands;
            }

            // The aggregate creates the join state (if needed) and counts this token when it
            // processes the command, then evaluates the condition against the new count.
            commands.Add(new EvaluateActivationConditionCommand(ActivationCondition));

            // Mark as executing so the execution loop doesn't re-process this entry.
            // The async activation condition callback will complete or discard this entry.
            await activityContext.Execute();
        }

        return commands;
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
