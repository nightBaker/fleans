using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Domain.Aggregates.Services;

public record BoundaryEventFiredResult(
    bool IsInterrupting,
    string AttachedToActivityId,
    Guid HostActivityInstanceId,
    IReadOnlyList<IInfrastructureEffect> TimerEffects);

public class BoundaryEventHandler
{
    private readonly WorkflowInstanceState _state;
    private readonly Action<IDomainEvent> _emit;

    public BoundaryEventHandler(
        WorkflowInstanceState state,
        Action<IDomainEvent> emit)
    {
        _state = state;
        _emit = emit;
    }

    /// <summary>
    /// Handles boundary event firing. For interrupting events: cancels the attached activity
    /// and emits variable scope operations. For non-interrupting: clones variables and handles
    /// timer cycle management. Returns a result so the aggregate can call shared utilities
    /// (CancelScopeChildren, BuildBoundaryUnsubscribeEffects, BuildUserTaskCleanupEffects).
    /// </summary>
    public BoundaryEventFiredResult HandleFired(
        Activity boundaryActivity,
        string attachedToActivityId,
        bool isInterrupting,
        ActivityInstanceEntry hostEntry,
        ExpandoObject deliveredVariables)
    {
        var timerEffects = new List<IInfrastructureEffect>();

        Guid variablesId;
        Guid? scopeId;

        if (isInterrupting)
        {
            // Cancel the attached activity
            _emit(new ActivityCancelled(
                hostEntry.ActivityInstanceId,
                $"Interrupted by boundary event '{boundaryActivity.ActivityId}'"));

            // Use the attached activity's variables scope
            variablesId = hostEntry.VariablesId;
            scopeId = hostEntry.ScopeId;
        }
        else
        {
            // Non-interrupting: leave attached activity running, clone variables
            var clonedScopeId = Guid.NewGuid();
            _emit(new VariableScopeCloned(clonedScopeId, hostEntry.VariablesId));
            variablesId = clonedScopeId;
            scopeId = hostEntry.ScopeId;

            // Merge delivered variables into cloned scope
            if (((IDictionary<string, object?>)deliveredVariables).Count > 0)
            {
                _emit(new VariablesMerged(clonedScopeId, deliveredVariables));
            }

            // For non-interrupting cycle timers, re-register the timer with decremented count
            if (boundaryActivity is BoundaryTimerEvent boundaryTimer
                && boundaryTimer.TimerDefinition.Type == TimerType.Cycle)
            {
                // Use tracked cycle state if available; otherwise use original definition for first fire
                var currentCycle = _state.GetTimerCycleState(
                    hostEntry.ActivityInstanceId, boundaryTimer.ActivityId)
                    ?? boundaryTimer.TimerDefinition;

                var nextCycle = currentCycle.DecrementCycle();
                _emit(new TimerCycleUpdated(
                    hostEntry.ActivityInstanceId, boundaryTimer.ActivityId, nextCycle));

                if (nextCycle is not null)
                {
                    timerEffects.Add(new RegisterTimerEffect(
                        _state.Id, hostEntry.ActivityInstanceId,
                        boundaryTimer.ActivityId, nextCycle.GetDueTime()));
                }
            }
        }

        // Spawn the boundary event activity
        _emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: boundaryActivity.ActivityId,
            ActivityType: boundaryActivity.GetType().Name,
            VariablesId: variablesId,
            ScopeId: scopeId,
            MultiInstanceIndex: null,
            TokenId: null));

        return new BoundaryEventFiredResult(
            isInterrupting,
            attachedToActivityId,
            hostEntry.ActivityInstanceId,
            timerEffects);
    }
}
