namespace Fleans.Persistence.Entities;

public class ConditionSequenceEntity
{
    public string ConditionalSequenceFlowId { get; set; } = null!;
    public bool Result { get; set; }
    public bool IsEvaluated { get; set; }
    public Guid WorkflowInstanceId { get; set; }
    public Guid GatewayActivityInstanceId { get; set; }
}
