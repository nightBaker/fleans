namespace Fleans.ServiceDefaults.DTOs;

/// <param name="ActivityInstanceId">
/// The activity instance to complete — the <c>activityInstanceId</c> of an external task record.
/// With it a repeated completion is ignored instead of completing a later instance of the same
/// activity; without it the active instance of <paramref name="ActivityId"/> completes.
/// </param>
public record CompleteActivityRequest(
    Guid WorkflowInstanceId,
    string ActivityId,
    Dictionary<string, object>? Variables = null,
    Guid? ActivityInstanceId = null);
