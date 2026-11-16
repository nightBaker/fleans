using Fleans.Domain.Activities;
using Fleans.Domain.States;

namespace Fleans.Domain.InstanceMigration;

/// <summary>
/// Decides whether an instance can move to another version of its process (#1069). A pure
/// function of the instance state, both definitions and the plan: it changes nothing, so the dry
/// run and the migration itself run the same checks.
/// </summary>
/// <remarks>
/// Each active element maps to its explicit pair, else to the element with the same id. The
/// mapped element must exist, have the same type, sit in the mapped scope, keep its
/// multi-instance kind, call the same process (call activity) and the same task type (custom
/// task). Joins with waiting tokens, in-flight work and running compensation reject the instance:
/// the caller retries later, or uses instance modification for what cannot be mapped.
/// </remarks>
public static class InstanceMigrationValidator
{
    public static List<MigrationRejection> Validate(
        WorkflowInstanceState state, IWorkflowDefinition source, IWorkflowDefinition target, MigrationPlan plan)
    {
        var rejections = new List<MigrationRejection>();

        if (!state.IsStarted || state.IsCompleted)
        {
            rejections.Add(new(MigrationRejectionCodes.NotRunning, null,
                "The instance is not running (completed, failed or cancelled)."));
            return rejections;
        }
        if (source.WorkflowId != target.WorkflowId)
        {
            rejections.Add(new(MigrationRejectionCodes.OtherProcess, null,
                "The target is a version of a different process."));
            return rejections;
        }
        if (state.ProcessDefinitionId == target.ProcessDefinitionId)
        {
            rejections.Add(new(MigrationRejectionCodes.SameVersion, null,
                "The instance already runs the target version."));
            return rejections;
        }

        ValidateMappings(source, target, plan, rejections);

        if (state.ActiveCompensationWalks.Count > 0)
            rejections.Add(new(MigrationRejectionCodes.CompensationRunning, null,
                "A compensation is running; retry once it has finished."));

        ValidateInFlightWork(state, rejections);

        foreach (var entry in state.GetActiveActivities())
            ValidateActiveEntry(entry, source, target, plan, rejections);

        foreach (var join in state.ComplexGatewayJoinStates)
            ValidateJoin(join.GatewayActivityId, source, target, plan, rejections);

        foreach (var snapshot in state.CompensationLog.Where(s => !s.IsCompensated))
        {
            var targetId = plan.MapActivityId(snapshot.ActivityDefinitionId);
            if (MigrationWaits.FindElement(target, targetId) is null)
                rejections.Add(new(MigrationRejectionCodes.UnmappedActivity, snapshot.ActivityDefinitionId,
                    $"Completed compensable activity '{snapshot.ActivityDefinitionId}' has no element '{targetId}' in the target, so it could not be compensated."));
        }

        ValidateEventSubProcesses(state, source, target, plan, rejections);

        return rejections.Distinct().ToList();
    }

    private static void ValidateMappings(
        IWorkflowDefinition source, IWorkflowDefinition target, MigrationPlan plan, List<MigrationRejection> rejections)
    {
        foreach (var duplicate in plan.Mappings.GroupBy(m => m.SourceActivityId).Where(g => g.Select(m => m.TargetActivityId).Distinct().Count() > 1))
            rejections.Add(new(MigrationRejectionCodes.InvalidMapping, duplicate.Key,
                $"Element '{duplicate.Key}' is mapped to more than one target element."));

        foreach (var mapping in plan.Mappings)
        {
            if (MigrationWaits.FindElement(source, mapping.SourceActivityId) is null)
                rejections.Add(new(MigrationRejectionCodes.InvalidMapping, mapping.SourceActivityId,
                    $"Mapping source '{mapping.SourceActivityId}' is not an element of the current version."));
            if (MigrationWaits.FindElement(target, mapping.TargetActivityId) is null)
                rejections.Add(new(MigrationRejectionCodes.InvalidMapping, mapping.SourceActivityId,
                    $"Mapping target '{mapping.TargetActivityId}' is not an element of the target version."));
        }
    }

    private static void ValidateInFlightWork(WorkflowInstanceState state, List<MigrationRejection> rejections)
    {
        if (state.PendingOperations.Count > 0)
            rejections.Add(new(MigrationRejectionCodes.InFlightWork, null,
                "The instance has queued callbacks that are not applied yet; retry later."));

        foreach (var script in state.GetActiveActivities().Where(e => e.IsExecuting && e.ActivityType == nameof(ScriptTask)))
            rejections.Add(new(MigrationRejectionCodes.InFlightWork, script.ActivityId,
                $"Script task '{script.ActivityId}' is running; retry once it has finished."));

        foreach (var condition in state.ConditionSequenceStates.Where(c => !c.IsEvaluated && state.HasActiveEntry(c.GatewayActivityInstanceId)))
        {
            var gateway = state.GetActiveEntry(condition.GatewayActivityInstanceId);
            rejections.Add(new(MigrationRejectionCodes.InFlightWork, gateway.ActivityId,
                $"Gateway '{gateway.ActivityId}' is evaluating its conditions; retry later."));
        }
    }

    private static void ValidateActiveEntry(
        ActivityInstanceEntry entry, IWorkflowDefinition source, IWorkflowDefinition target,
        MigrationPlan plan, List<MigrationRejection> rejections)
    {
        var sourceElement = MigrationWaits.FindElement(source, entry.ActivityId);
        var targetId = plan.MapActivityId(entry.ActivityId);
        var targetElement = MigrationWaits.FindElement(target, targetId);
        if (sourceElement is null || targetElement is null)
        {
            rejections.Add(new(MigrationRejectionCodes.UnmappedActivity, entry.ActivityId,
                $"Active element '{entry.ActivityId}' has no element '{targetId}' in the target; map it explicitly or use instance modification."));
            return;
        }

        if (sourceElement is MultiInstanceActivity != targetElement is MultiInstanceActivity)
        {
            rejections.Add(new(MigrationRejectionCodes.MultiInstanceChanged, entry.ActivityId,
                $"Element '{entry.ActivityId}' changes between multi-instance and single instance."));
            return;
        }
        if (sourceElement is MultiInstanceActivity sourceMi && targetElement is MultiInstanceActivity targetMi
            && (sourceMi.IsSequential != targetMi.IsSequential || sourceMi.InnerActivity.GetType() != targetMi.InnerActivity.GetType()))
        {
            rejections.Add(new(MigrationRejectionCodes.MultiInstanceChanged, entry.ActivityId,
                $"Multi-instance element '{entry.ActivityId}' changes its kind (sequential/parallel) or its inner activity type."));
            return;
        }
        if (sourceElement.GetType() != targetElement.GetType())
        {
            rejections.Add(new(MigrationRejectionCodes.TypeChanged, entry.ActivityId,
                $"Element '{entry.ActivityId}' is a {sourceElement.GetType().Name}, target '{targetId}' is a {targetElement.GetType().Name}."));
            return;
        }

        var sourceScope = MigrationWaits.ScopeIdOf(source, entry.ActivityId);
        var targetScope = MigrationWaits.ScopeIdOf(target, targetId);
        var mappedSourceScope = sourceScope is null ? null : plan.MapActivityId(sourceScope);
        if (mappedSourceScope != targetScope)
            rejections.Add(new(MigrationRejectionCodes.ScopeChanged, entry.ActivityId,
                $"Element '{entry.ActivityId}' moves from scope '{sourceScope ?? "(process)"}' to scope '{targetScope ?? "(process)"}'."));

        var isIteration = entry.MultiInstanceIndex is not null;
        var sourceRun = sourceElement is MultiInstanceActivity sm && isIteration ? sm.InnerActivity : sourceElement;
        var targetRun = targetElement is MultiInstanceActivity tm && isIteration ? tm.InnerActivity : targetElement;

        if (sourceRun is CallActivity sourceCall && targetRun is CallActivity targetCall
            && sourceCall.CalledProcessKey != targetCall.CalledProcessKey)
            rejections.Add(new(MigrationRejectionCodes.CallActivityChanged, entry.ActivityId,
                $"Call activity '{entry.ActivityId}' calls a different process in the target."));

        if (sourceRun is CustomTaskActivity sourceTask && targetRun is CustomTaskActivity targetTask
            && sourceTask.TaskType != targetTask.TaskType)
            rejections.Add(new(MigrationRejectionCodes.ExternalTaskTypeChanged, entry.ActivityId,
                $"Custom task '{entry.ActivityId}' has a different task type in the target."));

        if (sourceElement.IsJoinGateway)
            ValidateJoin(entry.ActivityId, source, target, plan, rejections);
    }

    /// <summary>
    /// A join with waiting tokens counts arrivals by the ids of its incoming flows' sources, and
    /// finished entries keep their source ids — so its incoming flows (and a complex gateway's
    /// activation condition) must stay exactly as they are.
    /// </summary>
    private static void ValidateJoin(
        string gatewayId, IWorkflowDefinition source, IWorkflowDefinition target,
        MigrationPlan plan, List<MigrationRejection> rejections)
    {
        var targetId = plan.MapActivityId(gatewayId);
        var sourceGateway = MigrationWaits.FindElement(source, gatewayId);
        var targetGateway = MigrationWaits.FindElement(target, targetId);
        if (sourceGateway is null || targetGateway is null)
        {
            rejections.Add(new(MigrationRejectionCodes.UnmappedActivity, gatewayId,
                $"Join '{gatewayId}' with waiting tokens has no element '{targetId}' in the target."));
            return;
        }

        var sourceIncoming = IncomingSources(source, sourceGateway);
        var targetIncoming = IncomingSources(target, targetGateway);
        var conditionChanged = sourceGateway is ComplexGateway sc && targetGateway is ComplexGateway tc
            && sc.ActivationCondition != tc.ActivationCondition;
        if (!sourceIncoming.SetEquals(targetIncoming) || conditionChanged || sourceGateway.GetType() != targetGateway.GetType())
            rejections.Add(new(MigrationRejectionCodes.JoinChanged, gatewayId,
                $"Join '{gatewayId}' has waiting tokens and its incoming flows or activation condition change in the target."));
    }

    private static HashSet<string> IncomingSources(IWorkflowDefinition root, Activity gateway)
    {
        var scope = root.FindScopeForActivity(gateway.ActivityId) ?? root;
        return scope.SequenceFlows.Where(f => f.Target.ActivityId == gateway.ActivityId)
            .Select(f => f.Source.ActivityId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Event sub-process listeners are armed per open scope (the process, each running
    /// sub-process). v1 keeps them as they are, so they must not change in an open scope.
    /// </summary>
    private static void ValidateEventSubProcesses(
        WorkflowInstanceState state, IWorkflowDefinition source, IWorkflowDefinition target,
        MigrationPlan plan, List<MigrationRejection> rejections)
    {
        if (!MigrationWaits.EventSubProcessListeners(source, source).SequenceEqual(MigrationWaits.EventSubProcessListeners(target, target)))
            rejections.Add(new(MigrationRejectionCodes.EventSubProcessChanged, null,
                "The process-level event sub-processes' timer, message or signal start events change in the target."));

        foreach (var host in state.GetActiveActivities().Where(e => e.IsExecuting))
        {
            if (MigrationWaits.FindElement(source, host.ActivityId) is not SubProcess sourceScope)
                continue;
            var targetScope = MigrationWaits.FindElement(target, plan.MapActivityId(host.ActivityId)) as SubProcess;
            var sourceListeners = MigrationWaits.EventSubProcessListeners(sourceScope, source);
            var targetListeners = targetScope is null ? [] : MigrationWaits.EventSubProcessListeners(targetScope, target);
            if (!sourceListeners.SequenceEqual(targetListeners))
                rejections.Add(new(MigrationRejectionCodes.EventSubProcessChanged, host.ActivityId,
                    $"The event sub-processes' start events in running sub-process '{host.ActivityId}' change in the target."));
        }
    }
}
