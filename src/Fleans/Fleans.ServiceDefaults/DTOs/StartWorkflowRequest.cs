namespace Fleans.ServiceDefaults.DTOs;

/// <param name="BusinessId">Optional business identifier (an order number, a contract id),
/// immutable for the instance's lifetime; at most 256 characters (#824).</param>
public record StartWorkflowRequest(string WorkflowId, Dictionary<string, object?>? Variables = null, string? BusinessId = null);
