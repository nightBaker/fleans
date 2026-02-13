namespace Fleans.Domain.States;

[GenerateSerializer]
public record ActivityInstanceEntry([property: Id(0)] Guid ActivityInstanceId, [property: Id(1)] string ActivityId);
