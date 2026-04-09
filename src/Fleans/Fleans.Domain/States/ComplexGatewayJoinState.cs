namespace Fleans.Domain.States;

[GenerateSerializer]
public class ComplexGatewayJoinState
{
    [Id(0)] public Guid ActivityInstanceId { get; private set; }
    [Id(1)] public int WaitingTokenCount { get; private set; }
    [Id(2)] public bool HasFired { get; private set; }
    [Id(3)] public string ActivationCondition { get; private set; } = string.Empty;

    public ComplexGatewayJoinState(Guid activityInstanceId, string activationCondition)
    {
        ActivityInstanceId = activityInstanceId;
        ActivationCondition = activationCondition;
    }

    private ComplexGatewayJoinState() { }

    public void Increment() => WaitingTokenCount++;
    public void MarkFired() => HasFired = true;
}
