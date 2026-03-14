using Fleans.Domain.Activities;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class TimerCycleTrackingState
{
    [Id(0)] public Guid HostActivityInstanceId { get; private set; }
    [Id(1)] public string TimerActivityId { get; private set; } = string.Empty;
    [Id(2)] public TimerType TimerType { get; private set; }
    [Id(3)] public string TimerExpression { get; private set; } = string.Empty;
    [Id(4)] public Guid WorkflowInstanceId { get; private set; }

    public TimerCycleTrackingState(Guid hostActivityInstanceId, string timerActivityId, TimerDefinition definition, Guid workflowInstanceId)
    {
        HostActivityInstanceId = hostActivityInstanceId;
        TimerActivityId = timerActivityId;
        TimerType = definition.Type;
        TimerExpression = definition.Expression;
        WorkflowInstanceId = workflowInstanceId;
    }

    private TimerCycleTrackingState() { }

    public TimerDefinition ToTimerDefinition() => new(TimerType, TimerExpression);

    public void UpdateFrom(TimerDefinition definition)
    {
        TimerType = definition.Type;
        TimerExpression = definition.Expression;
    }
}
