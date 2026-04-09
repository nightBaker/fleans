namespace Fleans.Domain.States;

[GenerateSerializer]
public class ComplexGatewayJoinState
{
    [Id(0)] public Guid ActivityInstanceId { get; private set; }
    [Id(1)] public int WaitingTokenCount { get; private set; }
    [Id(2)] public bool HasFired { get; private set; }
    [Id(3)] public string ActivationCondition { get; private set; } = string.Empty;
    [Id(4)] public Guid WorkflowInstanceId { get; private set; }

    public ComplexGatewayJoinState(Guid activityInstanceId, string activationCondition, Guid workflowInstanceId)
    {
        ActivityInstanceId = activityInstanceId;
        ActivationCondition = activationCondition;
        WorkflowInstanceId = workflowInstanceId;
    }

    private ComplexGatewayJoinState() { }

    public void IncrementTokenCount() => WaitingTokenCount++;
    public void MarkFired() => HasFired = true;
}
