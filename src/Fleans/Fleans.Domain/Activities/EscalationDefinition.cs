namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record EscalationDefinition(
    [property: Id(0)] string Id,
    [property: Id(1)] string EscalationCode,
    [property: Id(2)] string? Name);
