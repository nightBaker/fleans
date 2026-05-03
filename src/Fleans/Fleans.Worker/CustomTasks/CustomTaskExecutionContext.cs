namespace Fleans.Worker.CustomTasks;

/// <summary>
/// Per-event metadata passed to <see cref="CustomTaskHandlerBase.ExecuteAsync"/> so plugin
/// authors can correlate work with the specific workflow + activity instance — required for
/// idempotency keys, distributed tracing tags, deduplication keys against external systems,
/// and any other code that needs to identify "which execution of which activity is this."
/// </summary>
public sealed record CustomTaskExecutionContext(
    Guid WorkflowInstanceId,
    string WorkflowId,
    string? ProcessDefinitionId,
    Guid ActivityInstanceId,
    string ActivityId,
    string TaskType);
