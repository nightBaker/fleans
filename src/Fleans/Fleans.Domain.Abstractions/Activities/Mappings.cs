namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record InputMapping([property: Id(0)] string Source, [property: Id(1)] string Target);

[GenerateSerializer]
public record OutputMapping([property: Id(0)] string Source, [property: Id(1)] string Target);
