namespace Fleans.Application.Grains;

public interface ITimerStartEventSchedulerGrain : IGrainWithStringKey
{
    Task ActivateScheduler(string processDefinitionId);
    Task DeactivateScheduler();
    Task<Guid> FireTimerStartEvent();
}
