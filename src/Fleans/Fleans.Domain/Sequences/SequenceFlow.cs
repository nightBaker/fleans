using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences;

[GenerateSerializer]
public record SequenceFlow(
    [property: Id(0)] string SequenceFlowId,
    [property: Id(1)] Activity Source,
    [property: Id(2)] Activity Target);
