namespace Fleans.Domain.States;

[GenerateSerializer]
public class ActivityInstanceEntry
{
    public ActivityInstanceEntry(Guid activityInstanceId, string activityId, Guid workflowInstanceId, Guid? scopeId = null)
    {
        ActivityInstanceId = activityInstanceId;
        ActivityId = activityId;
        WorkflowInstanceId = workflowInstanceId;
        ScopeId = scopeId;
    }

    private ActivityInstanceEntry()
    {
    }

    [Id(0)]
    public Guid ActivityInstanceId { get; private set; }

    [Id(1)]
    public string ActivityId { get; private set; } = null!;

    [Id(2)]
    public Guid WorkflowInstanceId { get; private set; }

    [Id(3)]
    public bool IsCompleted { get; private set; }

    [Id(4)]
    public Guid? ChildWorkflowInstanceId { get; private set; }

    /// <summary>
    /// The scope that owns this activity instance.
    /// Null for root-level activities; set to the parent WorkflowScopeState.ScopeId for
    /// activities spawned inside a sub-process.
    /// </summary>
    [Id(5)]
    public Guid? ScopeId { get; private set; }

    public void SetChildWorkflowInstanceId(Guid childId) => ChildWorkflowInstanceId = childId;

    internal void MarkCompleted() => IsCompleted = true;
}
