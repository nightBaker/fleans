using Fleans.Domain.Activities;

namespace Fleans.Domain.InstanceMigration;

internal enum WaitKind
{
    Timer,
    Message,
    Signal,
    Conditional,
}

/// <summary>
/// One subscription an executing element holds while it waits: a timer reminder, a message or
/// signal subscription, or a conditional watcher. Mirrors what the element's <c>ExecuteAsync</c>
/// registers (its own catch definition plus its boundary events), so a migration can diff what the
/// source element registered against what the target element would have registered.
/// </summary>
/// <param name="Ordinal">Tells apart several waits of one kind on one element (a multiple event).</param>
/// <param name="Signature">The trigger definition; two waits with different signatures are "changed".</param>
internal sealed record Wait(
    WaitKind Kind,
    string ActivityId,
    int Ordinal,
    string Signature,
    bool IsBoundary,
    bool IsInterrupting,
    TimerDefinition? Timer = null,
    MessageDefinition? Message = null,
    string? SignalName = null,
    string? Condition = null)
{
    public (WaitKind, string, int) KeyIn(Func<string, string> map) => (Kind, map(ActivityId), Ordinal);
    public (WaitKind, string, int) Key => (Kind, ActivityId, Ordinal);
}

internal static class MigrationWaits
{
    /// <summary>The element with <paramref name="activityId"/> in any scope of <paramref name="definition"/>.</summary>
    public static Activity? FindElement(IWorkflowDefinition definition, string activityId)
        => definition.FindScopeForActivity(activityId)?.FindActivity(activityId);

    /// <summary>The id of the scope (sub-process, event sub-process) that contains the element; null at the root.</summary>
    public static string? ScopeIdOf(IWorkflowDefinition definition, string activityId)
        => definition.FindScopeForActivity(activityId) is { IsRootScope: false } scope && scope is Activity container
            ? container.ActivityId
            : null;

    /// <summary>
    /// The waits an executing entry of <paramref name="element"/> holds. An iteration of a
    /// multi-instance activity runs its inner activity; the host registers only its boundaries.
    /// </summary>
    public static List<Wait> WaitsOf(Activity element, IWorkflowDefinition definition, bool isMultiInstanceIteration)
    {
        var waits = new List<Wait>();
        var executed = element is MultiInstanceActivity mi && isMultiInstanceIteration ? mi.InnerActivity : element;

        switch (executed)
        {
            case TimerIntermediateCatchEvent timer:
                waits.Add(TimerWait(timer.ActivityId, 0, timer.TimerDefinition, isBoundary: false, isInterrupting: true));
                break;
            case MessageIntermediateCatchEvent message:
                waits.Add(MessageWait(message.ActivityId, 0, definition, message.MessageDefinitionId, false, true));
                break;
            case SignalIntermediateCatchEvent signal:
                waits.Add(SignalWait(signal.ActivityId, 0, definition, signal.SignalDefinitionId, false, true));
                break;
            case ConditionalIntermediateCatchEvent conditional:
                waits.Add(ConditionalWait(conditional.ActivityId, 0, conditional.ConditionExpression, false, true));
                break;
            case MultipleIntermediateCatchEvent multiple:
                waits.AddRange(MultipleWaits(multiple.ActivityId, multiple.Definitions, definition, false, true));
                break;
        }

        if (executed is BoundarableActivity)
        {
            var scope = definition.FindScopeForActivity(element.ActivityId) ?? definition;
            var attachedTo = executed.ActivityId;
            foreach (var b in scope.GetBoundaryTimerEvents(attachedTo))
                waits.Add(TimerWait(b.ActivityId, 0, b.TimerDefinition, true, b.IsInterrupting));
            foreach (var b in scope.GetBoundaryMessageEvents(attachedTo))
                waits.Add(MessageWait(b.ActivityId, 0, definition, b.MessageDefinitionId, true, b.IsInterrupting));
            foreach (var b in scope.GetBoundarySignalEvents(attachedTo))
                waits.Add(SignalWait(b.ActivityId, 0, definition, b.SignalDefinitionId, true, b.IsInterrupting));
            foreach (var b in scope.GetBoundaryConditionalEvents(attachedTo))
                waits.Add(ConditionalWait(b.ActivityId, 0, b.ConditionExpression, true, b.IsInterrupting));
            foreach (var b in scope.GetBoundaryMultipleEvents(attachedTo))
                waits.AddRange(MultipleWaits(b.ActivityId, b.Definitions, definition, true, b.IsInterrupting));
        }

        return waits;
    }

    /// <summary>
    /// The listener start events (timer, message, signal) of the event sub-processes declared
    /// directly in <paramref name="scope"/>, as comparable strings. Error and escalation event
    /// sub-processes hold no subscription and are resolved against the definition when they fire.
    /// </summary>
    public static List<string> EventSubProcessListeners(IWorkflowDefinition scope, IWorkflowDefinition root)
    {
        var listeners = new List<string>();
        foreach (var (esp, timer) in scope.GetEventSubProcessTimers())
            listeners.Add($"{esp.ActivityId}/{timer.ActivityId}/timer/{timer.TimerDefinition.Type}:{timer.TimerDefinition.Expression}");
        foreach (var (esp, message) in scope.GetEventSubProcessMessages())
        {
            var def = root.FindMessageDefinition(message.MessageDefinitionId);
            listeners.Add($"{esp.ActivityId}/{message.ActivityId}/message/{def?.Name}:{def?.CorrelationKeyExpression}");
        }
        foreach (var (esp, signal) in scope.GetEventSubProcessSignals())
            listeners.Add($"{esp.ActivityId}/{signal.ActivityId}/signal/{root.FindSignalDefinition(signal.SignalDefinitionId)?.Name}");
        listeners.Sort(StringComparer.Ordinal);
        return listeners;
    }

    private static IEnumerable<Wait> MultipleWaits(
        string activityId, List<EventDefinition> definitions, IWorkflowDefinition root, bool isBoundary, bool isInterrupting)
    {
        int timers = 0, messages = 0, signals = 0;
        foreach (var eventDef in definitions)
        {
            switch (eventDef)
            {
                case TimerEventDef t:
                    yield return TimerWait(activityId, timers++, t.TimerDefinition, isBoundary, isInterrupting);
                    break;
                case MessageEventDef m:
                    yield return MessageWait(activityId, messages++, root, m.MessageDefinitionId, isBoundary, isInterrupting);
                    break;
                case SignalEventDef s:
                    yield return SignalWait(activityId, signals++, root, s.SignalDefinitionId, isBoundary, isInterrupting);
                    break;
            }
        }
    }

    private static Wait TimerWait(string activityId, int ordinal, TimerDefinition timer, bool isBoundary, bool isInterrupting)
        => new(WaitKind.Timer, activityId, ordinal, $"{timer.Type}:{timer.Expression}", isBoundary, isInterrupting, Timer: timer);

    private static Wait MessageWait(
        string activityId, int ordinal, IWorkflowDefinition root, string messageDefinitionId, bool isBoundary, bool isInterrupting)
    {
        var message = root.GetMessageDefinition(messageDefinitionId);
        return new(WaitKind.Message, activityId, ordinal, $"{message.Name}:{message.CorrelationKeyExpression}",
            isBoundary, isInterrupting, Message: message);
    }

    private static Wait SignalWait(
        string activityId, int ordinal, IWorkflowDefinition root, string signalDefinitionId, bool isBoundary, bool isInterrupting)
    {
        var name = root.GetSignalDefinition(signalDefinitionId).Name;
        return new(WaitKind.Signal, activityId, ordinal, name, isBoundary, isInterrupting, SignalName: name);
    }

    private static Wait ConditionalWait(string activityId, int ordinal, string condition, bool isBoundary, bool isInterrupting)
        => new(WaitKind.Conditional, activityId, ordinal, condition, isBoundary, isInterrupting, Condition: condition);
}
