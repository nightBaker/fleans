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

    public ConditionalSequenceFlow ConditionalSequence { get; set; }

    [Id(1)]
    public bool Result { get; private set; }

    internal void SetResult(bool result)
    {
        Result = result;
    }
}