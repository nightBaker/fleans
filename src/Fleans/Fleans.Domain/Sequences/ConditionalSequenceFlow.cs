using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences;

[GenerateSerializer]
public record ConditionalSequenceFlow(
    string SequenceFlowId,
    Activity Source,
    Activity Target,
    [property: Id(3)] string Condition) : SequenceFlow(SequenceFlowId, Source, Target);
