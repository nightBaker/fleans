namespace Fleans.Domain.States;

[GenerateSerializer]
public class GatewayForkState
{
    [Id(0)] public Guid ForkInstanceId { get; private set; }
    [Id(1)] public Guid? ConsumedTokenId { get; private set; }
    [Id(2)] public List<Guid> CreatedTokenIds { get; private set; } = [];
    [Id(3)] public Guid WorkflowInstanceId { get; private set; }

    public GatewayForkState(Guid forkInstanceId, Guid? consumedTokenId, Guid workflowInstanceId)
    {
        ForkInstanceId = forkInstanceId;
        ConsumedTokenId = consumedTokenId;
        WorkflowInstanceId = workflowInstanceId;
    }

    private GatewayForkState() { }
}
