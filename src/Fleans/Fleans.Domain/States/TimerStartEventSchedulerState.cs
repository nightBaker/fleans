namespace Fleans.Domain.States;

[GenerateSerializer]
public class TimerStartEventSchedulerState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public string? ProcessDefinitionId { get; private set; }
    [Id(3)] public int FireCount { get; private set; }
    [Id(4)] public int? MaxFireCount { get; private set; }

    /// <summary>
    /// The instance the current tick starts, chosen and saved before the start (#751). A redelivered
    /// tick finds it and starts the same instance again — a no-op — instead of a second one.
    /// </summary>
    [Id(5)] public Guid? PendingInstanceId { get; private set; }

    public void PlanFire(Guid instanceId) => PendingInstanceId = instanceId;

    /// <summary>The planned instance started: the tick counts once and the plan is done.</summary>
    public void CompleteFire()
    {
        PendingInstanceId = null;
        FireCount++;
    }

    /// <summary>
    /// Starts a fresh schedule. The fire counter is reset so a re-activation (redeploy,
    /// disable → enable) gets the full number of repetitions again instead of inheriting
    /// the count from the previous schedule.
    /// </summary>
    public void Activate(string processDefinitionId, int? maxFireCount)
    {
        ProcessDefinitionId = processDefinitionId;
        MaxFireCount = maxFireCount;
        FireCount = 0;
        PendingInstanceId = null;
    }
}
