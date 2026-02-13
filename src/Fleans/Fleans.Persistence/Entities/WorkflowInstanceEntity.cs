namespace Fleans.Persistence.Entities;

public class WorkflowInstanceEntity
{
    public Guid Id { get; set; }
    public bool IsStarted { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? ExecutionStartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ETag { get; set; }

    public List<ActivityInstanceEntryEntity> Entries { get; set; } = [];
    public List<WorkflowVariablesEntity> VariableStates { get; set; } = [];
    public List<ConditionSequenceEntity> ConditionSequenceStates { get; set; } = [];
}
