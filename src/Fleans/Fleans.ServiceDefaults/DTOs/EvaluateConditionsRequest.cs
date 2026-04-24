namespace Fleans.ServiceDefaults.DTOs;

public record EvaluateConditionsRequest(string? WorkflowId, Dictionary<string, object>? Variables);
