namespace Fleans.Domain.States;

[GenerateSerializer]
public class TimerStartEventSchedulerState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public string? ProcessDefinitionId { get; private set; }
    [Id(3)] public int FireCount { get; private set; }
    [Id(4)] public int? MaxFireCount { get; private set; }

    public void Activate(string processDefinitionId, int? maxFireCount)
    {
        ProcessDefinitionId = processDefinitionId;
        MaxFireCount = maxFireCount;
    }

    public void IncrementFireCount() => FireCount++;
}
