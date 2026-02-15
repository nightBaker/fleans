using Orleans;

namespace Fleans.Application.QueryModels;

[GenerateSerializer]
public sealed record ProcessDefinitionSummary(
    [property: Id(0)] string ProcessDefinitionId,
    [property: Id(1)] string ProcessDefinitionKey,
    [property: Id(2)] int Version,
    [property: Id(3)] DateTimeOffset DeployedAt,
    [property: Id(4)] int ActivitiesCount,
    [property: Id(5)] int SequenceFlowsCount);
