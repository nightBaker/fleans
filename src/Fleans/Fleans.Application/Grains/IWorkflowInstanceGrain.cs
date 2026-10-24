using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.States;
using Orleans.Concurrency;
using System.Dynamic;

namespace Fleans.Application.Grains;

public interface IWorkflowInstanceGrain : IWorkflowInstanceCallback, IWorkflowExecutionContext
{
    // Re-declare inherited read-only methods with [ReadOnly] for Orleans concurrency optimization
    [ReadOnly]
    new ValueTask<Guid> GetWorkflowInstanceId();

    [ReadOnly]
    new ValueTask<string?> GetBusinessId();

    [ReadOnly]
    new ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();

    [ReadOnly]
    new ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();

    [ReadOnly]
    new ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();

    [ReadOnly]
    new ValueTask<object?> GetVariable(Guid variablesId, string variableName);

    Task CompleteActivity(string activityId, ExpandoObject variables);
    // CompleteActivity(activityId, activityInstanceId, variables) inherited from IWorkflowInstanceCallback
    Task FailActivity(string activityId, Exception exception);
    // FailActivity(activityId, activityInstanceId, exception) inherited from IWorkflowInstanceCallback

    /// <summary>
    /// Creates and starts this instance in one call and one journal append (#863): the definition,
    /// start activity (default: the process's start event), business id, input variables and — for
    /// a call-activity child — the parent link. Every start path calls this once, after the
    /// process-disabled check (<see cref="IProcessDefinitionGrain.GetStartableDefinition"/>).
    /// A repeated call for an instance already started from the same definition is a no-op.
    /// </summary>
    Task StartWorkflow(
        IWorkflowDefinition workflow,
        string? startActivityId = null,
        string? businessId = null,
        ExpandoObject? variables = null,
        ParentInfo? parent = null);

    // GetVariables(variablesStateId) inherited from IWorkflowInstanceCallback

    // operationId (#657): caller-derived deterministic op-id for at-least-once dedup.
    // The grain consults AppliedOperations/PendingOperations to avoid double-applying retries.
    [AlwaysInterleave]
    Task OnChildWorkflowCompleted(string operationId, string parentActivityId, ExpandoObject childVariables);
    [AlwaysInterleave]
    Task OnChildWorkflowFailed(string operationId, string parentActivityId, Exception exception);
    [AlwaysInterleave]
    Task<EscalationHandledResult> OnChildEscalationRaised(
        string operationId, Guid escalationInstanceId,
        Guid childWorkflowInstanceId, string hostActivityId,
        string escalationCode, ExpandoObject variables);

    // User task lifecycle
    Task ClaimUserTask(Guid activityInstanceId, string userId, IReadOnlyList<string> userGroups);
    Task UnclaimUserTask(Guid activityInstanceId);
    Task CompleteUserTask(Guid activityInstanceId, string userId, ExpandoObject variables);
    Task FailUserTask(Guid activityInstanceId, string errorCode, string errorMessage);
    Task CancelUserTask(Guid activityInstanceId, string? reason);

    Task HandleMessageDelivery(string activityId, Guid hostActivityInstanceId, ExpandoObject variables);
    Task HandleBoundaryMessageFired(string boundaryActivityId, Guid hostActivityInstanceId);
    Task<TimeSpan?> HandleTimerFired(string timerActivityId, Guid hostActivityInstanceId);
    [AlwaysInterleave]
    Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId);
    [AlwaysInterleave]
    Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId);

    /// <summary>
    /// Returns the compensation log — one entry per completed compensable activity,
    /// ordered ascending by <see cref="CompensationLogEntrySnapshot.CompletedAtSequence"/>.
    /// Each entry is annotated with its handler activity ID (resolved from the BPMN model)
    /// and the variable snapshot captured at the activity's completion time.
    /// </summary>
    [ReadOnly]
    ValueTask<IReadOnlyList<CompensationLogEntrySnapshot>> GetCompensationLog();

    /// <summary>
    /// The instance's activities, variables and condition results, for the admin UI and the REST
    /// API. The read model holds these only once the instance completes; until then they come from here.
    /// </summary>
    [ReadOnly]
    ValueTask<InstanceStateSnapshot> GetStateSnapshot();
}
