namespace Fleans.Domain.States;

[GenerateSerializer]
public class ConditionalStartEventListenerState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public string ProcessDefinitionKey { get; set; } = string.Empty;
    [Id(3)] public string ActivityId { get; set; } = string.Empty;
    [Id(4)] public string ConditionExpression { get; set; } = string.Empty;
    [Id(5)] public bool IsRegistered { get; set; }
}
