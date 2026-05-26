namespace Fleans.Domain.States;

[GenerateSerializer]
public enum MessageSubscriptionStatus
{
    Empty,
    Subscribed,
    Delivering
}

[GenerateSerializer]
public record PendingMessageIntent(
    [property: Id(0)] MessageSubscription Subscription);

[GenerateSerializer]
public class MessageCorrelationState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public MessageSubscription? Subscription { get; set; }
    [Id(3)] public MessageSubscriptionStatus Status { get; set; } = MessageSubscriptionStatus.Empty;
    [Id(4)] public PendingMessageIntent? Pending { get; set; }
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
