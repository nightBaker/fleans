namespace Fleans.Domain.Events;

/// <summary>
/// Published by a workflow instance after an append that changes its read model: the journal of
/// <paramref name="InstanceId"/> now holds <paramref name="Version"/> events. The read-model
/// projector consumes it, reads the journal itself and projects up to its head (#861). It carries
/// no state, so it stays small, and a late or repeated one is harmless.
/// </summary>
[GenerateSerializer]
public sealed record InstanceAdvanced(
    [property: Id(0)] Guid InstanceId,
    [property: Id(1)] int Version);
