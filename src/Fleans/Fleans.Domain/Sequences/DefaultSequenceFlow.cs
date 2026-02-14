using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences;

[GenerateSerializer]
public class DefaultSequenceFlow : SequenceFlow
{
    public DefaultSequenceFlow(string sequenceFlowId, Activity source, Activity target)
        : base(sequenceFlowId, source, target)
    {
    }
}
