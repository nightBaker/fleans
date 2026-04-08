using System;
using System.Collections.Generic;

using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain
{
    public interface IWorkflowDefinition
    {
        string WorkflowId { get; }
        string? ProcessDefinitionId { get; }
        List<Activity> Activities { get; }
        List<SequenceFlow> SequenceFlows { get; }
        List<MessageDefinition> Messages { get; }
        List<SignalDefinition> Signals { get; }
        bool IsRootScope { get; }
        Activity GetActivity(string activityId);

        Activity? FindActivity(string activityId)
            => Activities.FirstOrDefault(a => a.ActivityId == activityId);

        Activity GetStartActivity()
            => Activities.FirstOrDefault(a => a is StartEvent or TimerStartEvent or MessageStartEvent or SignalStartEvent)
                ?? throw new InvalidOperationException(
                    "Workflow must have a StartEvent, TimerStartEvent, MessageStartEvent, or SignalStartEvent");

        SequenceFlow? GetOutgoingFlow(Activity activity)
            => SequenceFlows.FirstOrDefault(sf => sf.Source == activity);

        IEnumerable<SequenceFlow> GetOutgoingFlows(Activity activity)
            => SequenceFlows.Where(sf => sf.Source == activity);

        IEnumerable<SequenceFlow> GetIncomingFlows(Activity activity)
            => SequenceFlows.Where(sf => sf.Target == activity);

        SequenceFlow GetSequenceFlow(string sequenceFlowId)
            => SequenceFlows.First(sf => sf.SequenceFlowId == sequenceFlowId);

        MessageDefinition GetMessageDefinition(string messageDefinitionId)
            => Messages.First(m => m.Id == messageDefinitionId);

        MessageDefinition? FindMessageDefinition(string messageDefinitionId)
            => Messages.FirstOrDefault(m => m.Id == messageDefinitionId);

        SignalDefinition GetSignalDefinition(string signalDefinitionId)
            => Signals.First(s => s.Id == signalDefinitionId);

        SignalDefinition? FindSignalDefinition(string signalDefinitionId)
            => Signals.FirstOrDefault(s => s.Id == signalDefinitionId);

        bool HasTimerStartEvent()
            => Activities.OfType<TimerStartEvent>().Any();

        HashSet<string> GetMessageStartEventNames()
            => Activities.OfType<MessageStartEvent>()
                .Select(ms => FindMessageDefinition(ms.MessageDefinitionId)?.Name)
                .OfType<string>()
                .ToHashSet();

        HashSet<string> GetSignalStartEventNames()
            => Activities.OfType<SignalStartEvent>()
                .Select(ss => FindSignalDefinition(ss.SignalDefinitionId)?.Name)
                .OfType<string>()
                .ToHashSet();

        IEnumerable<BoundaryTimerEvent> GetBoundaryTimerEvents(string attachedToActivityId)
            => Activities.OfType<BoundaryTimerEvent>()
                .Where(b => b.AttachedToActivityId == attachedToActivityId);

        IEnumerable<MessageBoundaryEvent> GetBoundaryMessageEvents(string attachedToActivityId)
            => Activities.OfType<MessageBoundaryEvent>()
                .Where(b => b.AttachedToActivityId == attachedToActivityId);

        IEnumerable<SignalBoundaryEvent> GetBoundarySignalEvents(string attachedToActivityId)
            => Activities.OfType<SignalBoundaryEvent>()
                .Where(b => b.AttachedToActivityId == attachedToActivityId);

        /// <summary>
        /// Recursively searches this definition and nested SubProcess scopes
        /// for the scope that directly contains the given activity.
        /// Returns null if not found.
        /// </summary>
        IWorkflowDefinition? FindScopeForActivity(string activityId)
        {
            if (Activities.Any(a => a.ActivityId == activityId))
                return this;

            foreach (var subProcess in Activities.OfType<SubProcess>())
            {
                var result = ((IWorkflowDefinition)subProcess).FindScopeForActivity(activityId);
                if (result is not null)
                    return result;
            }

            foreach (var evtSub in Activities.OfType<EventSubProcess>())
            {
                var result = ((IWorkflowDefinition)evtSub).FindScopeForActivity(activityId);
                if (result is not null)
                    return result;
            }

            foreach (var mi in Activities.OfType<MultiInstanceActivity>())
            {
                if (mi.InnerActivity is IWorkflowDefinition innerScope)
                {
                    var result = innerScope.FindScopeForActivity(activityId);
                    if (result is not null)
                        return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Same as <see cref="FindScopeForActivity"/> but throws if the activity is not found.
        /// </summary>
        IWorkflowDefinition GetScopeForActivity(string activityId)
            => FindScopeForActivity(activityId)
                ?? throw new InvalidOperationException($"Activity '{activityId}' not found in any definition scope");

        /// <summary>
        /// Finds the activity by searching all scopes recursively.
        /// </summary>
        Activity GetActivityAcrossScopes(string activityId)
            => GetScopeForActivity(activityId).GetActivity(activityId);

        /// <summary>
        /// Finds the matching BoundaryErrorEvent for a failed activity, searching the activity's
        /// scope and walking up parent SubProcess scopes if not found.
        /// Specific error code matches take priority over catch-all (null ErrorCode) boundaries.
        /// </summary>
        (BoundaryErrorEvent BoundaryEvent, IWorkflowDefinition Scope, string AttachedToActivityId)?
            FindBoundaryErrorHandler(string failedActivityId, string? errorCode)
        {
            var targetActivityId = failedActivityId;

            while (true)
            {
                var scope = FindScopeForActivity(targetActivityId);
                if (scope is null) return null;

                var candidates = scope.Activities
                    .OfType<BoundaryErrorEvent>()
                    .Where(b => b.AttachedToActivityId == targetActivityId
                        && (b.ErrorCode == null || b.ErrorCode == errorCode))
                    .ToList();

                // Prefer specific error code match over catch-all
                var match = candidates.FirstOrDefault(b => b.ErrorCode == errorCode)
                            ?? candidates.FirstOrDefault(b => b.ErrorCode == null);

                if (match is not null)
                    return (match, scope, targetActivityId);

                // Bubble up: if scope is a SubProcess, check its parent for boundary on the SubProcess
                if (scope is SubProcess subProcess)
                    targetActivityId = subProcess.ActivityId;
                else
                    return null; // at root scope, no match found
            }
        }

        /// <summary>
        /// Finds the matching EventSubProcess (containing an ErrorStartEvent) for a
        /// failed activity, searching the activity's scope and walking up through parent
        /// SubProcess / EventSubProcess scopes. Specific error code matches take priority
        /// over catch-all (null ErrorCode). Inner scopes take priority over outer scopes.
        /// An EventSubProcess never catches errors thrown from within itself (BPMN spec:
        /// event sub-processes only catch errors from the enclosing parent scope).
        /// </summary>
        (EventSubProcess EventSubProcess, IWorkflowDefinition EnclosingScope)?
            FindErrorEventSubProcessHandler(string failedActivityId, string? errorCode)
        {
            var targetActivityId = failedActivityId;
            // When we walk out of an EventSubProcess, we must not re-match it as a handler
            // in its own parent scope — that would allow an ESP to catch its own errors.
            string? escapedEventSubProcessId = null;

            while (true)
            {
                var scope = FindScopeForActivity(targetActivityId);
                if (scope is null) return null;

                var candidates = scope.Activities
                    .OfType<EventSubProcess>()
                    .Where(esp => esp.ActivityId != escapedEventSubProcessId)
                    .Where(esp => esp.Activities.OfType<ErrorStartEvent>().Any(ese =>
                        ese.ErrorCode == null || ese.ErrorCode == errorCode))
                    .ToList();

                var specific = candidates.FirstOrDefault(esp =>
                    esp.Activities.OfType<ErrorStartEvent>()
                        .Any(ese => ese.ErrorCode == errorCode));
                var catchAll = candidates.FirstOrDefault(esp =>
                    esp.Activities.OfType<ErrorStartEvent>()
                        .Any(ese => ese.ErrorCode == null));

                var match = specific ?? catchAll;
                if (match is not null)
                    return (match, scope);

                if (scope is SubProcess subProcess)
                {
                    targetActivityId = subProcess.ActivityId;
                    // Reset when walking out through a SubProcess so that the surrounding
                    // scope is considered fresh — a SubProcess can contain its own ESP
                    // children, and we must not skip them based on an inner ESP id.
                    escapedEventSubProcessId = null;
                }
                else if (scope is EventSubProcess outerEvtSub)
                {
                    escapedEventSubProcessId = outerEvtSub.ActivityId;
                    targetActivityId = outerEvtSub.ActivityId;
                }
                else
                    return null;
            }
        }

        /// <summary>
        /// Enumerates all <see cref="EventSubProcess"/> definitions declared directly in this
        /// scope whose start event is a <see cref="TimerStartEvent"/>. Does NOT recurse into
        /// nested sub-process scopes — callers register timers scope-by-scope.
        /// </summary>
        IEnumerable<(EventSubProcess EventSubProcess, TimerStartEvent TimerStart)> GetEventSubProcessTimers()
        {
            foreach (var esp in Activities.OfType<EventSubProcess>())
            {
                var timerStart = esp.Activities.OfType<TimerStartEvent>().FirstOrDefault();
                if (timerStart is not null)
                    yield return (esp, timerStart);
            }
        }

        /// <summary>
        /// Enumerates all <see cref="EventSubProcess"/> definitions declared directly in this
        /// scope whose start event is a <see cref="MessageStartEvent"/>. Does NOT recurse into
        /// nested sub-process scopes — callers register listeners scope-by-scope.
        /// </summary>
        IEnumerable<(EventSubProcess EventSubProcess, MessageStartEvent MessageStart)> GetEventSubProcessMessages()
        {
            foreach (var esp in Activities.OfType<EventSubProcess>())
            {
                var messageStart = esp.Activities.OfType<MessageStartEvent>().FirstOrDefault();
                if (messageStart is not null)
                    yield return (esp, messageStart);
            }
        }

        /// <summary>
        /// Enumerates all <see cref="EventSubProcess"/> definitions declared directly in this
        /// scope whose start event is a <see cref="SignalStartEvent"/>. Does NOT recurse into
        /// nested sub-process scopes — callers register listeners scope-by-scope.
        /// </summary>
        IEnumerable<(EventSubProcess EventSubProcess, SignalStartEvent SignalStart)> GetEventSubProcessSignals()
        {
            foreach (var esp in Activities.OfType<EventSubProcess>())
            {
                var signalStart = esp.Activities.OfType<SignalStartEvent>().FirstOrDefault();
                if (signalStart is not null)
                    yield return (esp, signalStart);
            }
        }

        /// <summary>
        /// Locates the <see cref="EventSubProcess"/> that declares the given start-event activity
        /// id, along with the enclosing scope it lives inside. Used on timer fire to route from
        /// the raw timer activity id back to the event sub-process container and its parent scope.
        /// Returns null if the activity id does not match any event sub-process start event.
        /// </summary>
        (EventSubProcess EventSubProcess, IWorkflowDefinition EnclosingScope)?
            FindEventSubProcessByStartEvent(string startEventActivityId)
        {
            foreach (var esp in Activities.OfType<EventSubProcess>())
            {
                if (esp.Activities.Any(a => a.ActivityId == startEventActivityId))
                    return (esp, this);
            }

            foreach (var subProcess in Activities.OfType<SubProcess>())
            {
                var nested = ((IWorkflowDefinition)subProcess).FindEventSubProcessByStartEvent(startEventActivityId);
                if (nested is not null) return nested;
            }

            foreach (var mi in Activities.OfType<MultiInstanceActivity>())
            {
                if (mi.InnerActivity is IWorkflowDefinition innerScope)
                {
                    var nested = innerScope.FindEventSubProcessByStartEvent(startEventActivityId);
                    if (nested is not null) return nested;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns activity IDs of sibling catch events that compete with the given activity
        /// after an EventBasedGateway. Returns empty set if the activity is not downstream
        /// of an EventBasedGateway.
        /// </summary>
        IReadOnlySet<string> GetEventBasedGatewaySiblings(string completedActivityId)
        {
            var scope = FindScopeForActivity(completedActivityId);
            if (scope is null) return new HashSet<string>();

            var gatewayFlow = scope.SequenceFlows
                .Where(sf => sf.Target.ActivityId == completedActivityId)
                .FirstOrDefault(sf => sf.Source is EventBasedGateway);

            if (gatewayFlow?.Source is not EventBasedGateway gateway)
                return new HashSet<string>();

            return scope.SequenceFlows
                .Where(sf => sf.Source == gateway && sf.Target.ActivityId != completedActivityId)
                .Select(sf => sf.Target.ActivityId)
                .ToHashSet();
        }
    }
   
    [GenerateSerializer]
    public record WorkflowDefinition : IWorkflowDefinition
    {
        [Id(0)]
        public required string WorkflowId { get; init; }

        [Id(1)]
        public required List<Activity> Activities { get; init; }

        [Id(2)]
        public required List<SequenceFlow> SequenceFlows { get; init; }

        [Id(3)]
        public string? ProcessDefinitionId { get; init; }

        [Id(4)]
        public List<MessageDefinition> Messages { get; init; } = [];

        [Id(5)]
        public List<SignalDefinition> Signals { get; init; } = [];

        public bool IsRootScope => true;

        public Activity GetActivity(string activityId)
            => Activities.First(a => a.ActivityId == activityId);
    }
}
