namespace Fleans.Application.Grains;

/// <summary>
/// The workflow instance's activation found the journal ahead of its state after a failed or
/// conflicting append, so it rejects calls until it deactivates (#845). Nothing of the call was
/// applied; retry it, and it reaches the next activation, which replays the journal. An
/// <see cref="OrleansException"/>, so callers that retry transient Orleans failures retry this too.
/// </summary>
[GenerateSerializer]
[Alias("Fleans.Application.Grains.WorkflowInstanceStaleException")]
public sealed class WorkflowInstanceStaleException : OrleansException
{
    public WorkflowInstanceStaleException(string message) : base(message) { }
}
