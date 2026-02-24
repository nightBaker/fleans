/// <summary>
/// First-class domain aggregate that models a sub-process scope.
/// Tracks the scope's identity, parent scope relationship, variable binding,
/// and active child instances to allow O(1) scope completion detection.
/// </summary>
[GenerateSerializer]
public class WorkflowScopeState
{
    private WorkflowScopeState() { }

    public WorkflowScopeState(
        Guid scopeId,
        Guid? parentScopeId,
        Guid variablesId,
        string subProcessActivityId,
        Guid subProcessActivityInstanceId)
    {
        ScopeId = scopeId;
        ParentScopeId = parentScopeId;
        VariablesId = variablesId;
        SubProcessActivityId = subProcessActivityId;
        SubProcessActivityInstanceId = subProcessActivityInstanceId;
    }

    /// <summary>Unique identity of this scope instance.</summary>
    [Id(0)]
    public Guid ScopeId { get; private set; }

    /// <summary>Parent scope, or null if this scope is directly under the root process.</summary>
    [Id(1)]
    public Guid? ParentScopeId { get; private set; }

    /// <summary>The variable scope bound to this process scope (child of parent's VariablesId).</summary>
    [Id(2)]
    public Guid VariablesId { get; private set; }

    /// <summary>The sub-process activity definition id that created this scope.</summary>
    [Id(3)]
    public string SubProcessActivityId { get; private set; } = null!;

    /// <summary>The activity instance id of the sub-process that created this scope.</summary>
    [Id(4)]
    public Guid SubProcessActivityInstanceId { get; private set; }

    /// <summary>
    /// Set of active child activity instance ids owned by this scope.
    /// Used for O(1) completion detection and deterministic cancellation.
    /// </summary>
    [Id(5)]
    public HashSet<Guid> ActiveChildInstanceIds { get; private set; } = [];

    /// <summary>True when all child activity instances in this scope have completed.</summary>
    public bool IsComplete => ActiveChildInstanceIds.Count == 0;

    /// <summary>Registers a new child activity instance in this scope.</summary>
    public void TrackChild(Guid activityInstanceId)
        => ActiveChildInstanceIds.Add(activityInstanceId);

    /// <summary>
    /// Removes a child activity instance from tracking.
    /// Returns true if the scope now has no remaining active children (scope complete).
    /// </summary>
    public bool UntrackChild(Guid activityInstanceId)
    {
        ActiveChildInstanceIds.Remove(activityInstanceId);
        return IsComplete;
    }

    /// <summary>
    /// Returns all currently active child instance ids and clears the set.
    /// Used for deterministic scope cancellation.
    /// </summary>
    public IReadOnlyList<Guid> DrainActiveChildren()
    {
        var children = ActiveChildInstanceIds.ToList();
        ActiveChildInstanceIds.Clear();
        return children;
    }
}