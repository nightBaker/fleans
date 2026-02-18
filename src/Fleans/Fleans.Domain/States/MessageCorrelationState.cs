namespace Fleans.Domain.States;

[GenerateSerializer]
public class MessageCorrelationState
{
    [Id(0)]
    public Dictionary<string, MessageSubscription> Subscriptions { get; set; } = new();
}

[GenerateSerializer]
public record MessageSubscription(
    [property: Id(0)] Guid WorkflowInstanceId,
    [property: Id(1)] string ActivityId);
