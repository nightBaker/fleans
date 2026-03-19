namespace Fleans.ServiceDefaults.DTOs;

public record CompleteActivityRequest(
    Guid WorkflowInstanceId,
    string ActivityId,
    Dictionary<string, object>? Variables = null);
