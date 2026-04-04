using Orleans;

namespace Fleans.Application.Effects;

public interface IEffectContext
{
    IGrainFactory GrainFactory { get; }
    Guid WorkflowInstanceId { get; }
    Task PersistStateAsync();
    Task ProcessFailureEffects(string activityId, Guid hostActivityInstanceId, Exception ex);
}
