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
    /// <summary>The scope instance the join runs in: each multi-instance iteration or re-entered scope has its own join (#839).</summary>
    [Id(6)] public Guid? ScopeId { get; private set; }
    /// <summary>Tokens the fork sent towards this join, known once it fires; the state goes once they have all arrived (#839).</summary>
    [Id(7)] public int? ExpectedTokenCount { get; private set; }

    public ComplexGatewayJoinState(string gatewayActivityId, Guid firstActivityInstanceId, string activationCondition, Guid workflowInstanceId, Guid? scopeId = null)
    {
        GatewayActivityId = gatewayActivityId;
        ScopeId = scopeId;
        FirstActivityInstanceId = firstActivityInstanceId;
        ActivationCondition = activationCondition;
        WorkflowInstanceId = workflowInstanceId;
    }

    private ComplexGatewayJoinState() { }

    public void IncrementTokenCount() => WaitingTokenCount++;
    public void MarkFired(int? expectedTokenCount = null)
    {
        HasFired = true;
        ExpectedTokenCount = expectedTokenCount;
    }

    /// <summary>True once the join fired and every token its fork sent has arrived.</summary>
    public bool AllTokensArrived => HasFired && ExpectedTokenCount is { } expected && WaitingTokenCount >= expected;

    /// <summary>A migration mapped the element to another id (#1069).</summary>
    internal void RenameActivity(string activityId) => GatewayActivityId = activityId;
}
