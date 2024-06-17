using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences;


[GenerateSerializer]
public record ConditionalSequenceFlow : SequenceFlow
{
    [Id(3)]
    public string Condition { get; init; }

    public ConditionalSequenceFlow(string sequencId, Activity source, Activity target, string condition) 
        : base(sequencId, source, target)
    {
        Condition = condition;        
    }
}
