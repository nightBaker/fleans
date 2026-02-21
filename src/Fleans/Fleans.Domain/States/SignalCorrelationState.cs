namespace Fleans.Domain.States;

[GenerateSerializer]
public class SignalCorrelationState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public List<SignalSubscription> Subscriptions { get; private set; } = new();
}

[GenerateSerializer]
public record SignalSubscription(
    [property: Id(0)] Guid WorkflowInstanceId,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] Guid HostActivityInstanceId);
