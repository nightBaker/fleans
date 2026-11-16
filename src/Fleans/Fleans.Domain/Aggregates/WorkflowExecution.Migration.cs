using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.InstanceMigration;
using Fleans.Domain.States;

namespace Fleans.Domain.Aggregates;

// Instance migration (#1069): moves a running instance to another version of its process in one
// transition. Design: docs/plans/2026-11-16-instance-migration-design.md.
public partial class WorkflowExecution
{
    /// <summary>
    /// Validates the migration without changing anything. Empty when the instance can move.
    /// </summary>
    public List<MigrationRejection> ValidateMigration(MigrationPlan plan, IWorkflowDefinition target)
        => InstanceMigrationValidator.Validate(_state, _definition, target, plan);

    /// <summary>
    /// Moves the instance to <paramref name="target"/>: validates, emits <see cref="InstanceMigrated"/>
    /// (which renames the running state), then rewires what each waiting element subscribed to so
    /// it matches the target element. Throws <see cref="InstanceMigrationRejectedException"/>
    /// before emitting anything when the validator rejects the instance.
    /// </summary>
    /// <remarks>
    /// This aggregate keeps the source definition: the caller builds a new aggregate on
    /// <paramref name="target"/> for the next transition. Mapped waits are kept as they are;
    /// renamed ones are moved to the new id with the same due time; with
    /// <see cref="MigrationPlan.UpdateEventTriggers"/> the changed ones are re-armed from the target.
    /// </remarks>
    public IReadOnlyList<IInfrastructureEffect> Migrate(MigrationPlan plan, IWorkflowDefinition target, string migrationId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);

        var rejections = ValidateMigration(plan, target);
        if (rejections.Count > 0)
            throw new InstanceMigrationRejectedException(rejections);

        // The waits are read from the source ids, before InstanceMigrated renames the entries.
        var waiting = _state.GetActiveActivities()
            .Where(e => e.IsExecuting)
            .Select(e => (Entry: e, SourceActivityId: e.ActivityId))
            .ToList();

        Emit(new InstanceMigrated(
            _state.ProcessDefinitionId ?? _definition.ProcessDefinitionId ?? string.Empty,
            target.ProcessDefinitionId ?? string.Empty,
            plan.RenamingMappings(),
            migrationId));

        var cleanups = new List<IInfrastructureEffect>();
        var registrations = new List<IInfrastructureEffect>();
        var now = DateTimeOffset.UtcNow;
        foreach (var (entry, sourceActivityId) in waiting)
        {
            var sourceElement = MigrationWaits.FindElement(_definition, sourceActivityId)!;
            var targetElement = MigrationWaits.FindElement(target, entry.ActivityId)!;
            var isIteration = entry.MultiInstanceIndex is not null;

            RewireWaits(entry, sourceElement, targetElement, target, plan, isIteration, now, cleanups, registrations);

            if (_state.UserTasks.ContainsKey(entry.ActivityInstanceId))
                registrations.AddRange(RedefineUserTask(entry, sourceActivityId, sourceElement, targetElement, isIteration));
        }

        // Cleanups first: a renamed message wait unsubscribes its key before subscribing it again.
        return cleanups.Concat(registrations).ToList().AsReadOnly();
    }

    private void RewireWaits(
        ActivityInstanceEntry entry, Activity sourceElement, Activity targetElement, IWorkflowDefinition target,
        MigrationPlan plan, bool isIteration, DateTimeOffset now,
        List<IInfrastructureEffect> cleanups, List<IInfrastructureEffect> registrations)
    {
        var sourceWaits = MigrationWaits.WaitsOf(sourceElement, _definition, isIteration);
        var targetWaits = MigrationWaits.WaitsOf(targetElement, target, isIteration);
        var targetByKey = targetWaits.GroupBy(w => w.Key).ToDictionary(g => g.Key, g => g.First());
        var matched = new HashSet<(WaitKind, string, int)>();
        var conditionalsChanged = false;

        foreach (var sourceWait in sourceWaits)
        {
            var consumed = IsConsumed(entry, sourceWait, plan);
            if (targetByKey.TryGetValue(sourceWait.KeyIn(plan.MapActivityId), out var targetWait))
            {
                matched.Add(targetWait.Key);
                var renamed = sourceWait.ActivityId != targetWait.ActivityId;
                var rearm = plan.UpdateEventTriggers && sourceWait.Signature != targetWait.Signature;
                if ((!renamed && !rearm) || consumed)
                    continue; // kept exactly as it is

                if (sourceWait.Kind == WaitKind.Conditional)
                {
                    conditionalsChanged |= rearm; // a rename alone is applied to the watcher by InstanceMigrated
                    continue;
                }
                AddCleanup(entry, sourceWait, cleanups);
                AddRegistration(entry, targetWait, rearm ? null : sourceWait, target, now, registrations);
            }
            else if (!consumed)
            {
                if (sourceWait.Kind == WaitKind.Conditional)
                    conditionalsChanged = true;
                else
                    AddCleanup(entry, sourceWait, cleanups);
            }
        }

        foreach (var targetWait in targetWaits.Where(w => !matched.Contains(w.Key)))
        {
            if (targetWait.Kind == WaitKind.Conditional)
                conditionalsChanged = true;
            else
                AddRegistration(entry, targetWait, keepFrom: null, target, now, registrations);
        }

        if (conditionalsChanged)
            RewireConditionalWatchers(entry, targetWaits, plan);
    }

    /// <summary>
    /// A non-interrupting boundary event that already fired holds no subscription any more (a
    /// cycle timer still does until its last repetition), so a migration has nothing to move.
    /// </summary>
    private bool IsConsumed(ActivityInstanceEntry host, Wait wait, MigrationPlan plan)
    {
        if (!wait.IsBoundary || wait.IsInterrupting)
            return false;

        var mappedId = plan.MapActivityId(wait.ActivityId);
        var since = host.ExecutionStartedAt ?? host.CreatedAt ?? DateTimeOffset.MinValue;
        var fired = _state.Entries.Any(e =>
            (e.ActivityId == wait.ActivityId || e.ActivityId == mappedId)
            && e.ScopeId == host.ScopeId
            && e.CreatedAt >= since);
        if (!fired)
            return false;

        return wait.Timer is not { Type: TimerType.Cycle }
            || _state.GetTimerCycleState(host.ActivityInstanceId, mappedId) is null;
    }

    private void AddCleanup(ActivityInstanceEntry entry, Wait wait, List<IInfrastructureEffect> cleanups)
    {
        switch (wait.Kind)
        {
            case WaitKind.Timer:
                cleanups.Add(new UnregisterTimerEffect(_state.Id, entry.ActivityInstanceId, wait.ActivityId));
                break;
            case WaitKind.Message:
                // Cleanup is best effort: a correlation variable that is gone means the key cannot
                // be rebuilt; the old subscription then only ever delivers to a stale guard.
                if (TryResolveCorrelationKey(wait.Message!, entry.VariablesId) is { } key)
                    cleanups.Add(new UnsubscribeMessageEffect(wait.Message!.Name, key));
                break;
            case WaitKind.Signal:
                cleanups.Add(new UnsubscribeSignalEffect(wait.SignalName!, _state.Id, wait.ActivityId));
                break;
        }
    }

    /// <param name="keepFrom">
    /// The source wait whose deadline a moved timer keeps; null re-arms it from the target definition.
    /// </param>
    private void AddRegistration(
        ActivityInstanceEntry entry, Wait wait, Wait? keepFrom, IWorkflowDefinition target, DateTimeOffset now,
        List<IInfrastructureEffect> registrations)
    {
        switch (wait.Kind)
        {
            case WaitKind.Timer:
                if (keepFrom is null && _state.GetTimerCycleState(entry.ActivityInstanceId, wait.ActivityId) is not null)
                    Emit(new TimerCycleUpdated(entry.ActivityInstanceId, wait.ActivityId, null)); // a re-armed cycle restarts
                var dueTime = keepFrom is null
                    ? wait.Timer!.GetDueTime()
                    : RemainingDueTime(entry, keepFrom.Timer!, wait.ActivityId, now);
                registrations.Add(new RegisterTimerEffect(_state.Id, entry.ActivityInstanceId, wait.ActivityId, dueTime));
                break;
            case WaitKind.Message:
                // An unresolvable correlation key goes out empty: the handler rejects it as a
                // registration failure and fails the activity (docs/conventions/invariants.md) —
                // by then the grain runs on the target definition, which this aggregate does not.
                registrations.Add(new SubscribeMessageEffect(
                    wait.Message!.Name, TryResolveCorrelationKey(wait.Message!, entry.VariablesId) ?? string.Empty,
                    _state.Id, wait.ActivityId, entry.ActivityInstanceId));
                break;
            case WaitKind.Signal:
                registrations.Add(new SubscribeSignalEffect(wait.SignalName!, _state.Id, wait.ActivityId, entry.ActivityInstanceId));
                break;
        }
    }

    /// <summary>
    /// The time left until a moved timer's original deadline: a duration counts from when its
    /// host started waiting, a date stays absolute, a cycle keeps its phase.
    /// </summary>
    private TimeSpan RemainingDueTime(ActivityInstanceEntry entry, TimerDefinition sourceTimer, string currentTimerId, DateTimeOffset now)
    {
        var since = entry.ExecutionStartedAt ?? entry.CreatedAt ?? now;
        var elapsed = now - since;
        switch (sourceTimer.Type)
        {
            case TimerType.Date:
                return sourceTimer.GetDueTime();
            case TimerType.Cycle:
                var interval = (_state.GetTimerCycleState(entry.ActivityInstanceId, currentTimerId) ?? sourceTimer).ParseCycle().Interval;
                if (interval <= TimeSpan.Zero)
                    return TimeSpan.Zero;
                return interval - TimeSpan.FromTicks(Math.Max(0, elapsed.Ticks) % interval.Ticks);
            default:
                var remaining = sourceTimer.GetDueTime() - elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    private void RewireConditionalWatchers(ActivityInstanceEntry entry, List<Wait> targetWaits, MigrationPlan plan)
    {
        var existing = _state.ConditionalWatchers.FirstOrDefault(w => w.ActivityInstanceId == entry.ActivityInstanceId);
        var variablesId = existing?.VariablesId ?? entry.VariablesId;
        if (existing is not null)
            Emit(new ConditionalWatcherCleared(entry.ActivityInstanceId));

        foreach (var wait in targetWaits.Where(w => w.Kind == WaitKind.Conditional && !IsConsumed(entry, w, plan)))
            Emit(new ConditionalWatcherRegistered(entry.ActivityInstanceId, wait.ActivityId, wait.Condition!, variablesId));
    }

    /// <summary>
    /// Gives a waiting user task the target's assignment. The claim stays when the claimant still
    /// qualifies: the target is unrestricted, names them as assignee or candidate user, or keeps
    /// every candidate group the source had (they may have claimed through one — group membership
    /// is not known here). Otherwise the task is unclaimed.
    /// </summary>
    private List<IInfrastructureEffect> RedefineUserTask(
        ActivityInstanceEntry entry, string sourceActivityId, Activity sourceElement, Activity targetElement, bool isIteration)
    {
        var effects = new List<IInfrastructureEffect>();
        if (Executed(sourceElement, isIteration) is not UserTask source || Executed(targetElement, isIteration) is not UserTask target)
            return effects;

        var assignmentChanged = source.Assignee != target.Assignee
            || !source.CandidateGroups.SequenceEqual(target.CandidateGroups)
            || !source.CandidateUsers.SequenceEqual(target.CandidateUsers)
            || !(source.ExpectedOutputVariables ?? []).SequenceEqual(target.ExpectedOutputVariables ?? []);
        if (!assignmentChanged && sourceActivityId == entry.ActivityId)
            return effects;

        if (assignmentChanged)
            Emit(new UserTaskRedefined(entry.ActivityInstanceId, target.Assignee,
                target.CandidateGroups.ToList(), target.CandidateUsers.ToList(), target.ExpectedOutputVariables?.ToList()));

        effects.Add(new RedefineUserTaskEffect(entry.ActivityInstanceId, entry.ActivityId, target.Assignee,
            target.CandidateGroups.ToList(), target.CandidateUsers.ToList(), target.ExpectedOutputVariables?.ToList()));

        var task = _state.UserTasks[entry.ActivityInstanceId];
        if (task.TaskState == UserTaskLifecycleState.Claimed && task.ClaimedBy is { } claimant
            && !ClaimantStillQualifies(claimant, source, target))
        {
            Emit(new UserTaskUnclaimed(entry.ActivityInstanceId));
            effects.Add(new UpdateUserTaskClaimEffect(entry.ActivityInstanceId, null, UserTaskLifecycleState.Created));
        }
        return effects;
    }

    private static bool ClaimantStillQualifies(string claimant, UserTask source, UserTask target)
    {
        var unrestricted = target.Assignee is null && target.CandidateUsers.Count == 0 && target.CandidateGroups.Count == 0;
        return unrestricted
            || target.Assignee == claimant
            || target.CandidateUsers.Contains(claimant)
            || (source.CandidateGroups.Count > 0 && source.CandidateGroups.All(g => target.CandidateGroups.Contains(g, StringComparer.Ordinal)));
    }

    private static Activity Executed(Activity element, bool isIteration)
        => element is MultiInstanceActivity mi && isIteration ? mi.InnerActivity : element;

    private string? TryResolveCorrelationKey(MessageDefinition message, Guid variablesId)
    {
        try
        {
            return ResolveCorrelationKey(message, variablesId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The current id of an element a callback addresses: the id itself when the definition has
    /// it, else the id a migration renamed it to (a reminder or subscription armed before it).
    /// </summary>
    private string CurrentActivityId(string activityId)
        => _definition.FindScopeForActivity(activityId) is not null
            ? activityId
            : _state.ResolveMigratedActivityId(activityId);

    /// <summary>True when the definition has no element <paramref name="activityId"/> — a wait a migration removed.</summary>
    private bool IsUnknownActivity(string activityId) => _definition.FindScopeForActivity(activityId) is null;
}
