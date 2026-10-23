namespace Fleans.Domain;

/// <summary>
/// The call activity that started a child instance: the parent instance and the call activity's id.
/// Passed to the instance's start so the link is journaled with the start itself (#863).
/// </summary>
[GenerateSerializer]
public sealed record ParentInfo(
    [property: Id(0)] Guid InstanceId,
    [property: Id(1)] string ActivityId);
