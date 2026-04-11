namespace Fleans.Domain.States;

[GenerateSerializer]
public class ComplexGatewayJoinState
{
    [Id(0)] public string GatewayActivityId { get; private set; } = string.Empty;
    [Id(1)] public int WaitingTokenCount { get; private set; }
    [Id(2)] public bool HasFired { get; private set; }
    [Id(3)] public string? ActivationCondition { get; private set; }
    [Id(4)] public Guid WorkflowInstanceId { get; private set; }
    [Id(5)] public Guid FirstActivityInstanceId { get; private set; }

    public ComplexGatewayJoinState(string gatewayActivityId, Guid firstActivityInstanceId, string activationCondition, Guid workflowInstanceId)
    {
        GatewayActivityId = gatewayActivityId;
        FirstActivityInstanceId = firstActivityInstanceId;
        ActivationCondition = activationCondition;
        WorkflowInstanceId = workflowInstanceId;
    }

    private ComplexGatewayJoinState() { }

    public void IncrementTokenCount() => WaitingTokenCount++;
    public void MarkFired() => HasFired = true;
}
