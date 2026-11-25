namespace Fleans.Application;

/// <summary>
/// The start call to a new instance timed out (#1040). The instance may still be starting — a wide
/// first turn can outlast the grain-call timeout — so the caller gets its id to look it up, instead
/// of retrying and starting a second one.
/// </summary>
public sealed class StartOutcomeUnknownException(Guid workflowInstanceId, Exception inner)
    : TimeoutException($"The start of workflow instance {workflowInstanceId} did not answer in time; it may still be starting.", inner)
{
    public Guid WorkflowInstanceId { get; } = workflowInstanceId;
}
