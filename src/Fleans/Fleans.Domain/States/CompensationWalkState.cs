namespace Fleans.Domain.States;

/// <summary>
/// Tracks the in-progress state of a compensation walk.
/// There is at most one active walk per workflow instance at a time.
/// Walk progress is derived from <see cref="WorkflowInstanceState.CompensationLog"/> entries
/// (via <see cref="CompletedActivitySnapshot.IsCompensated"/>) rather than a separate
/// "remaining" list, keeping the state event-sourcing safe.
/// </summary>
[GenerateSerializer]
public class CompensationWalkState
{
    private CompensationWalkState() { }

    public CompensationWalkState(Guid? scopeId, string? targetActivityRef)
    {
        ScopeId = scopeId;
        TargetActivityRef = targetActivityRef;
    }

    /// <summary>
    /// The scope instance ID the walk operates on (null = root scope).
    /// </summary>
    [Id(0)]
    public Guid? ScopeId { get; private set; }

    /// <summary>
    /// If set, only the named activity definition is compensated (targeted throw).
    /// If null, all compensable activities in the scope are compensated (broadcast).
    /// </summary>
    [Id(1)]
    public string? TargetActivityRef { get; private set; }

    /// <summary>
    /// The instance ID of the compensation handler activity currently executing.
    /// Null when no handler is actively running.
    /// </summary>
    [Id(2)]
    public Guid? CurrentHandlerInstanceId { get; private set; }

    internal void SetCurrentHandler(Guid handlerInstanceId)
        => CurrentHandlerInstanceId = handlerInstanceId;

    internal void ClearCurrentHandler()
        => CurrentHandlerInstanceId = null;
}
