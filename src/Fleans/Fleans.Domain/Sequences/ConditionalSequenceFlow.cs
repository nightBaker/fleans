using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences;


[GenerateSerializer]
public class ConditionalSequenceFlow : SequenceFlow
{
    [Id(3)]
    public string Condition { get; set; }

    public ConditionalSequenceFlow(string sequencId, Activity source, Activity target, string condition)
        : base(sequencId, source, target)
    {
        Condition = condition;
    }
}
