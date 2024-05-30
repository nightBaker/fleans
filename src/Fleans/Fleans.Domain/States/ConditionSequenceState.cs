using Fleans.Domain.Sequences;

namespace Fleans.Domain.States;

public class ConditionSequenceState
{
    public ConditionSequenceState(ConditionalSequenceFlow conditionalSequence)
    {
        ConditionalSequence = conditionalSequence;
    }

    public Guid ConditionSequenceStateId { get; } = Guid.NewGuid();

    public ConditionalSequenceFlow ConditionalSequence { get; set; }

    public bool Result { get; private set; }

    internal void SetResult(bool result)
    {
        Result = result;
    }
}