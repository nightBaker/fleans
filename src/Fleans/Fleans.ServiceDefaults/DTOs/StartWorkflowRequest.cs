namespace Fleans.ServiceDefaults.DTOs;

public record StartWorkflowRequest(string WorkflowId, Dictionary<string, object?>? Variables = null);
