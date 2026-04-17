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

    public CompensationWalkState(Guid? scopeId, string? targetActivityRef, Guid throwerActivityInstanceId)
    {
        ScopeId = scopeId;
        TargetActivityRef = targetActivityRef;
        ThrowerActivityInstanceId = throwerActivityInstanceId;
    }

    [Id(0)]
    public Guid? ScopeId { get; private set; }

    [Id(1)]
    public string? TargetActivityRef { get; private set; }

    /// <summary>
    /// The instance ID of the compensation handler activity currently executing.
    /// Null when no handler is actively running.
    /// </summary>
    [Id(2)]
    public Guid? CurrentHandlerInstanceId { get; private set; }

    /// <summary>
    /// The instance ID of the throw/end event that initiated this walk.
    /// Used to complete the thrower and resume its outgoing flow after the walk finishes.
    /// </summary>
    [Id(3)]
    public Guid ThrowerActivityInstanceId { get; private set; }

    internal void SetCurrentHandler(Guid handlerInstanceId)
        => CurrentHandlerInstanceId = handlerInstanceId;

    internal void ClearCurrentHandler()
        => CurrentHandlerInstanceId = null;
}
