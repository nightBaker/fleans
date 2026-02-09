using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record ConditionalGateway : Gateway
{
    protected ConditionalGateway(string ActivityId) : base(ActivityId)
    {
    }

    internal async Task<bool> SetConditionResult(
        IWorkflowInstance workflowInstance,
        IActivityInstance activityInstance,
        string conditionSequenceFlowId,
        bool result)
    {
        var state = await workflowInstance.GetState();
        var activityInstanceId = await activityInstance.GetActivityInstanceId();
        await state.SetCondigitionSequencesResult(activityInstanceId, conditionSequenceFlowId, result);

        if (result)
            return true;

        var sequences = await state.GetConditionSequenceStates();
        var mySequences = sequences[activityInstanceId];

        if (mySequences.All(s => s.IsEvaluated))
        {
            var definition = await workflowInstance.GetWorkflowDefinition();
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
