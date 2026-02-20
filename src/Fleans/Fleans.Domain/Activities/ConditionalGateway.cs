using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record ConditionalGateway(string ActivityId) : Gateway(ActivityId)
{
    internal async Task<bool> SetConditionResult(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        string conditionSequenceFlowId,
        bool result,
        IWorkflowDefinition definition)
    {
        var activityInstanceId = await activityContext.GetActivityInstanceId();
        await workflowContext.SetConditionSequenceResult(activityInstanceId, conditionSequenceFlowId, result);

        if (result)
            return true;

        var sequences = await workflowContext.GetConditionSequenceStates();
        if (!sequences.TryGetValue(activityInstanceId, out var mySequences))
            return false;

        if (mySequences.All(s => s.IsEvaluated))
        {
            var hasDefault = definition.SequenceFlows
                .OfType<DefaultSequenceFlow>()
                .Any(sf => sf.Source.ActivityId == ActivityId);

            if (!hasDefault)
                throw new InvalidOperationException(
                    $"Gateway {ActivityId}: all conditions evaluated to false and no default flow exists");

            return true;
        }

        return false;
    }
}
