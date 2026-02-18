namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record MessageDefinition(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string? CorrelationKeyExpression);
