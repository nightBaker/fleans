namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record SignalDefinition(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name);
