using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences;

[GenerateSerializer]
public record SequenceFlow(string SequenceFlowId, Activity Source, Activity Target);
