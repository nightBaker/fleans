namespace Fleans.Domain.States;

[GenerateSerializer]
public class MessageCorrelationState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public List<MessageSubscription> Subscriptions { get; private set; } = new();
}

[GenerateSerializer]
public record MessageSubscription(
    [property: Id(0)] Guid WorkflowInstanceId,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] Guid HostActivityInstanceId,
    [property: Id(3)] string CorrelationKey)
{
    [Id(4)] public string MessageName { get; init; } = string.Empty;
}
