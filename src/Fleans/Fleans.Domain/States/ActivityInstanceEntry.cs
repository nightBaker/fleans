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

    [Id(5)]
    public Guid? ScopeId { get; private set; }

    [Id(6)]
    public int? MultiInstanceIndex { get; private set; }

    [Id(7)]
    public int? MultiInstanceTotal { get; private set; }

    public ActivityInstanceEntry(Guid activityInstanceId, string activityId, Guid workflowInstanceId, Guid? scopeId, int multiInstanceIndex)
        : this(activityInstanceId, activityId, workflowInstanceId, scopeId)
    {
        MultiInstanceIndex = multiInstanceIndex;
    }

    public void SetChildWorkflowInstanceId(Guid childId) => ChildWorkflowInstanceId = childId;

    internal void MarkCompleted() => IsCompleted = true;

    internal void SetMultiInstanceTotal(int total) => MultiInstanceTotal = total;
}
