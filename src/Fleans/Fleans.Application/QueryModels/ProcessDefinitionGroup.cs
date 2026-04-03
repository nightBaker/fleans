namespace Fleans.Application.QueryModels;

[GenerateSerializer]
public record ProcessDefinitionGroup(
    [property: Id(0)] string ProcessDefinitionKey,
    [property: Id(1)] IReadOnlyList<ProcessDefinitionSummary> Versions);
