using Orleans;

namespace Fleans.Application.Effects;

public interface IEffectContext
{
    IGrainFactory GrainFactory { get; }
    Guid WorkflowInstanceId { get; }

    /// <summary>
    /// Persists current grain state by draining uncommitted domain events and raising them.
    /// Call before external grain calls to ensure state is durable before side effects.
    /// </summary>
    Task PersistStateAsync();

    /// <summary>
    /// Fails the activity and dispatches any resulting effects. This causes recursive dispatch:
    /// Handler → FailActivity → new effects → DispatchAsync. This is intentional — failure
    /// handling may produce follow-up effects (e.g., timer unregistration) that must execute
    /// within the same grain turn.
    /// </summary>
    Task ProcessFailureEffects(string activityId, Guid hostActivityInstanceId, Exception ex);
}
