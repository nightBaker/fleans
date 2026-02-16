namespace Fleans.Domain;

[GenerateSerializer]
public record VariableMapping(
    [property: Id(0)] string Source,
    [property: Id(1)] string Target);
