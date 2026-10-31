namespace Fleans.ServiceDefaults.DTOs;

/// <summary>
/// Fails one activity instance with a BPMN error (#979). <paramref name="ErrorCode"/> is matched
/// by error boundary events and error event sub-processes; <paramref name="Variables"/> go to the
/// scope that catches it. A repeated or late failure is ignored.
/// </summary>
public record FailActivityRequest(
    Guid WorkflowInstanceId,
    string ActivityId,
    Guid ActivityInstanceId,
    string ErrorCode,
    string? ErrorMessage = null,
    Dictionary<string, object>? Variables = null);
