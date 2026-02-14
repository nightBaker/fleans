using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences;

[GenerateSerializer]
public class SequenceFlow
{
    [Id(0)]
    public string SequenceFlowId { get; set; }

    [Id(1)]
    public Activity Source { get; set; }

    [Id(2)]
    public Activity Target { get; set; }

    public SequenceFlow(string sequenceFlowId, Activity source, Activity target)
    {
        SequenceFlowId = sequenceFlowId;
        Source = source;
        Target = target;
    }
}
