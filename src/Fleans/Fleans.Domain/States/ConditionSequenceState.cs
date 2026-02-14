namespace Fleans.Domain.States;

[GenerateSerializer]
public class ConditionSequenceState
{
    public ConditionSequenceState(string conditionalSequenceFlowId, Guid gatewayActivityInstanceId, Guid workflowInstanceId)
    {
        ConditionalSequenceFlowId = conditionalSequenceFlowId;
        GatewayActivityInstanceId = gatewayActivityInstanceId;
        WorkflowInstanceId = workflowInstanceId;
    }

    private ConditionSequenceState()
    {
    }

    [Id(0)]
    public Guid GatewayActivityInstanceId { get; private set; }

    [Id(1)]
    public string ConditionalSequenceFlowId { get; private set; }

    [Id(2)]
    public bool Result { get; private set; }

    [Id(3)]
    public bool IsEvaluated { get; private set; }

    [Id(4)]
    public Guid WorkflowInstanceId { get; private set; }

    internal void SetResult(bool result)
    {
        Result = result;
        IsEvaluated = true;
    }
}
