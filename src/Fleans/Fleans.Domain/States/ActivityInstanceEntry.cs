namespace Fleans.Domain.States;

[GenerateSerializer]
public class ActivityInstanceEntry
{
    public ActivityInstanceEntry(Guid activityInstanceId, string activityId, Guid workflowInstanceId)
    {
        ActivityInstanceId = activityInstanceId;
        ActivityId = activityId;
        WorkflowInstanceId = workflowInstanceId;
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
    public Guid? ChildWorkflowInstanceId { get; internal set; }

    internal void MarkCompleted() => IsCompleted = true;
}
