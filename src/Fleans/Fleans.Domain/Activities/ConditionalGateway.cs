namespace Fleans.Domain.Activities;

/// <summary>A gateway whose outgoing flows carry conditions. The decision rule lives in
/// <c>WorkflowExecution.IsGatewayDecisionMade</c> (#864).</summary>
[GenerateSerializer]
public abstract record ConditionalGateway(string ActivityId) : Gateway(ActivityId);
