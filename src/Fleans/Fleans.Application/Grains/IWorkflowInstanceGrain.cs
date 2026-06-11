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
    new ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();

    [ReadOnly]
    new ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();

    [ReadOnly]
    new ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();

    [ReadOnly]
    new ValueTask<object?> GetVariable(Guid variablesId, string variableName);

    Task CompleteActivity(string activityId, ExpandoObject variables);
    // CompleteActivity(activityId, activityInstanceId, variables) inherited from IWorkflowInstanceCallback
    Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result);
    Task CompleteActivationCondition(string activityId, Guid activityInstanceId, bool result);
    Task FailActivity(string activityId, Exception exception);
    // FailActivity(activityId, activityInstanceId, exception) inherited from IWorkflowInstanceCallback
    Task StartWorkflow();
    Task SetWorkflow(IWorkflowDefinition workflow, string? startActivityId = null);

    // GetVariables(variablesStateId) inherited from IWorkflowInstanceCallback

    Task SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId);
    Task SetInitialVariables(ExpandoObject variables);
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

    Task CompleteMultiInstanceEarly(string hostActivityId, Guid hostActivityInstanceId);

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
    /// Returns the current Transaction Sub-Process outcomes for this workflow instance.
    /// Keyed by the activity instance ID of each Transaction host entry.
    /// </summary>
    [ReadOnly]
    ValueTask<IReadOnlyDictionary<Guid, TransactionOutcomeRecord>> GetTransactionOutcomes();

    /// <summary>
    /// Returns the compensation log — one entry per completed compensable activity,
    /// ordered ascending by <see cref="CompensationLogEntrySnapshot.CompletedAtSequence"/>.
    /// Each entry is annotated with its handler activity ID (resolved from the BPMN model)
    /// and the variable snapshot captured at the activity's completion time.
    /// </summary>
    [ReadOnly]
    ValueTask<IReadOnlyList<CompensationLogEntrySnapshot>> GetCompensationLog();

    /// <summary>Op-keys currently in the durable pending-events queue (PendingOperations). #657.</summary>
    [ReadOnly]
    ValueTask<IReadOnlyList<string>> GetPendingOperationKeys();

    /// <summary>Op-keys recorded in the dedup ledger (AppliedOperations). #657.</summary>
    [ReadOnly]
    ValueTask<IReadOnlyList<string>> GetAppliedOperationKeys();
}
