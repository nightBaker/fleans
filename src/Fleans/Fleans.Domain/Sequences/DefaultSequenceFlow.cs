using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences;

[GenerateSerializer]
public record DefaultSequenceFlow(
    string SequenceFlowId,
    Activity Source,
    Activity Target) : SequenceFlow(SequenceFlowId, Source, Target);
