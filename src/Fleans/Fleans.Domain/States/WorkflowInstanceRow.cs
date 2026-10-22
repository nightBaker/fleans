namespace Fleans.Domain.States;

/// <summary>
/// The scalar columns of an instance's <c>WorkflowInstances</c> read-model row — what lists and
/// filters query. Record equality answers "did this append change the row?", so a running instance
/// writes it only on the few events that touch these columns (start, parent link, execution start,
/// completion), not on every append (#878).
/// </summary>
/// <remarks>
/// Property names match <see cref="WorkflowInstanceState"/>'s mapped columns, so the read model
/// copies them by name. A new mapped scalar on the state must be added here too.
/// </remarks>
public sealed record WorkflowInstanceRow(
    string? ProcessDefinitionId,
    string? BusinessId,
    DateTimeOffset? CreatedAt,
    Guid? ParentWorkflowInstanceId,
    string? ParentActivityId,
    bool IsStarted,
    DateTimeOffset? ExecutionStartedAt,
    bool IsCompleted,
    bool IsCancelled,
    bool IsFailed,
    DateTimeOffset? CompletedAt)
{
    public static WorkflowInstanceRow From(WorkflowInstanceState state) => new(
        state.ProcessDefinitionId,
        state.BusinessId,
        ToStoredPrecision(state.CreatedAt),
        state.ParentWorkflowInstanceId,
        state.ParentActivityId,
        state.IsStarted,
        ToStoredPrecision(state.ExecutionStartedAt),
        state.IsCompleted,
        state.IsCancelled,
        state.IsFailed,
        ToStoredPrecision(state.CompletedAt));

    /// <summary>
    /// Postgres keeps microseconds, .NET ticks are 100 ns: a row read back would never equal one
    /// built from the state, and every activation would rewrite it. Truncating up front makes the
    /// stored value exactly the one compared.
    /// </summary>
    private static DateTimeOffset? ToStoredPrecision(DateTimeOffset? value) =>
        value?.AddTicks(-(value.Value.Ticks % TimeSpan.TicksPerMicrosecond));
}
