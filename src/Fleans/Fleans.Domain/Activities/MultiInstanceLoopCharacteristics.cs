namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record MultiInstanceLoopCharacteristics(
    [property: Id(0)] bool IsSequential,
    [property: Id(1)] int? LoopCardinality,
    [property: Id(2)] string? InputCollection,
    [property: Id(3)] string? InputDataItem,
    [property: Id(4)] string? OutputCollection,
    [property: Id(5)] string? OutputDataItem
);
