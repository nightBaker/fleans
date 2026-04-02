namespace Fleans.Application.QueryModels;

public record ProcessDefinitionGroup(
    string ProcessDefinitionKey,
    IReadOnlyList<ProcessDefinitionSummary> Versions);
