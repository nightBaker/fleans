namespace Fleans.Application.QueryModels;

/// <summary>
/// One entry in a workflow instance's compensation log — a snapshot of a completed
/// compensable activity, optionally annotated with the BPMN handler that compensates it.
/// Sourced from <see cref="Fleans.Domain.States.WorkflowInstanceState.CompensationLog"/>;
/// see <see cref="ICompensationLogService"/> for the admin-UI contract.
/// </summary>
[GenerateSerializer]
public sealed record CompensationLogEntrySnapshot(
    [property: Id(0)] Guid ActivityInstanceId,
    [property: Id(1)] string CompensableActivityId,
    [property: Id(2)] string? HandlerActivityId,
    [property: Id(3)] int CompletedAtSequence,
    [property: Id(4)] Guid? ScopeId,
    [property: Id(5)] bool IsCompensated,
    [property: Id(6)] Dictionary<string, string> Variables);
