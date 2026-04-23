using System.Dynamic;

namespace Fleans.Domain.States;

/// <summary>
/// A snapshot of a completed, compensable activity captured at its completion time.
/// Stored in the <see cref="WorkflowInstanceState.CompensationLog"/> and used by the
/// compensation walk to invoke handlers in reverse completion order.
/// </summary>
[GenerateSerializer]
public class CompletedActivitySnapshot
{
    private CompletedActivitySnapshot() { }

    public CompletedActivitySnapshot(
        Guid activityInstanceId,
        string activityDefinitionId,
        ExpandoObject variablesSnapshot,
        int completedAtSequence,
        Guid? scopeId)
    {
        ActivityInstanceId = activityInstanceId;
        ActivityDefinitionId = activityDefinitionId;
        VariablesSnapshot = variablesSnapshot;
        CompletedAtSequence = completedAtSequence;
        ScopeId = scopeId;
    }

    /// <summary>The runtime instance ID of the completed activity.</summary>
    [Id(0)]
    public Guid ActivityInstanceId { get; private set; }

    /// <summary>The definition ID of the completed activity (maps to a CompensationBoundaryEvent).</summary>
    [Id(1)]
    public string ActivityDefinitionId { get; private set; } = null!;

    /// <summary>
    /// Deep copy of the activity's scope variables at the moment of completion.
    /// Compensation handlers receive this snapshot — not the current scope state.
    /// </summary>
    [Id(2)]
    public ExpandoObject VariablesSnapshot { get; private set; } = null!;

    /// <summary>
    /// Monotonically increasing sequence number assigned at completion time.
    /// Used to sort snapshots in reverse completion order deterministically,
    /// even when parallel branches complete simultaneously.
    /// </summary>
    [Id(3)]
    public int CompletedAtSequence { get; private set; }

    /// <summary>
    /// The instance ID of the enclosing scope (SubProcess host activity instance ID),
    /// or null for root-scope activities.
    /// </summary>
    [Id(4)]
    public Guid? ScopeId { get; private set; }

    /// <summary>True once the compensation handler for this activity has run successfully.</summary>
    [Id(5)]
    public bool IsCompensated { get; private set; }

    internal void MarkCompensated() => IsCompensated = true;
}
