namespace Fleans.Domain.States;

[GenerateSerializer]
public class TimerStartEventSchedulerState
{
    [Id(0)] public string? ProcessDefinitionId { get; set; }
    [Id(1)] public int FireCount { get; set; }
    [Id(2)] public int? MaxFireCount { get; set; }
}
