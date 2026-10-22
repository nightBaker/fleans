namespace Fleans.Domain.States;

/// <summary>
/// A workflow instance's row in the read model (the <c>WorkflowInstances</c> table): the scalar
/// columns lists and filters need. Value equality tells whether the stored row is still current,
/// so the grain writes it only when it changes.
/// </summary>
/// <remarks>
/// Timestamps are kept at microsecond precision — what PostgreSQL stores — so a row read back
/// equals the row that was written.
/// </remarks>
public sealed record WorkflowInstanceRow(
    bool IsStarted,
    bool IsCompleted,
    bool IsCancelled,
    bool IsFailed,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExecutionStartedAt,
    DateTimeOffset? CompletedAt,
    string? ProcessDefinitionId,
    Guid? ParentWorkflowInstanceId,
    string? ParentActivityId,
    string? BusinessId)
{
    public static WorkflowInstanceRow From(WorkflowInstanceState state) => new(
        state.IsStarted,
        state.IsCompleted,
        state.IsCancelled,
        state.IsFailed,
        ToMicroseconds(state.CreatedAt),
        ToMicroseconds(state.ExecutionStartedAt),
        ToMicroseconds(state.CompletedAt),
        state.ProcessDefinitionId,
        state.ParentWorkflowInstanceId,
        state.ParentActivityId,
        state.BusinessId);

    private static DateTimeOffset? ToMicroseconds(DateTimeOffset? value) =>
        value is { } at ? at.AddTicks(-(at.Ticks % 10)) : null;
}
