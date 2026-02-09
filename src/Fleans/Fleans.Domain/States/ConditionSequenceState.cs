using Fleans.Domain.Sequences;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class ConditionSequenceState
{
    public ConditionSequenceState(ConditionalSequenceFlow conditionalSequence)
    {
        ConditionalSequence = conditionalSequence;
    }

    [Id(0)]
    public Guid ConditionSequenceStateId { get; } = Guid.NewGuid();

    [Id(1)]
    public ConditionalSequenceFlow ConditionalSequence { get; set; }

    [Id(2)]
    public bool Result { get; private set; }

    [Id(3)]
    public bool IsEvaluated { get; private set; }

    internal void SetResult(bool result)
    {
        Result = result;
        IsEvaluated = true;
    }
}