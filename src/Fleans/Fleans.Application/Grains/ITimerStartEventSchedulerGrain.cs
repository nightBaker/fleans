namespace Fleans.Application.Grains;

public interface ITimerStartEventSchedulerGrain : IGrainWithStringKey
{
    Task ActivateScheduler(string processDefinitionId);
    Task DeactivateScheduler();
    /// <summary>Starts one instance; <c>null</c> when the process is disabled and nothing started.</summary>
    Task<Guid?> FireTimerStartEvent();
}
