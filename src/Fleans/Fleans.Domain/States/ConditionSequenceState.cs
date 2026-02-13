namespace Fleans.Domain.States;

[GenerateSerializer]
public class ConditionSequenceState
{
    public ConditionSequenceState(string conditionalSequenceFlowId)
    {
        ConditionalSequenceFlowId = conditionalSequenceFlowId;
    }

    [Id(0)]
    public string ConditionalSequenceFlowId { get; private set; }

    [Id(1)]
    public bool Result { get; private set; }

    [Id(2)]
    public bool IsEvaluated { get; private set; }

    internal void SetResult(bool result)
    {
        Result = result;
        IsEvaluated = true;
    }
}