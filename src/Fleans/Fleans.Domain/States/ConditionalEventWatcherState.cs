namespace Fleans.Domain.States;

[GenerateSerializer]
public class ConditionalEventWatcherState
{
    [Id(0)]
    public Guid ActivityInstanceId { get; set; }

    [Id(1)]
    public string ActivityId { get; set; } = string.Empty;

    [Id(2)]
    public string ConditionExpression { get; set; } = string.Empty;

    [Id(3)]
    public Guid VariablesId { get; set; }

    [Id(4)]
    public bool LastEvaluatedResult { get; set; }
}
