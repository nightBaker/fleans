namespace Fleans.Domain.States;

[GenerateSerializer]
public class TimerStartEventSchedulerState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public string? ProcessDefinitionId { get; set; }
    [Id(3)] public int FireCount { get; set; }
    [Id(4)] public int? MaxFireCount { get; set; }
}
