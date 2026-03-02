using Fleans.Domain;
using Fleans.Domain.States;
using Orleans.Concurrency;
using System.Dynamic;

namespace Fleans.Application.Grains;

public interface IWorkflowInstanceGrain : IGrainWithGuidKey, IWorkflowExecutionContext
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
    Task CompleteActivity(string activityId, Guid activityInstanceId, ExpandoObject variables);
    Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result);
    Task FailActivity(string activityId, Exception exception);
    Task FailActivity(string activityId, Guid activityInstanceId, Exception exception);
    Task StartWorkflow();
    Task SetWorkflow(IWorkflowDefinition workflow);

    [ReadOnly]
    ValueTask<ExpandoObject> GetVariables(Guid variablesStateId);

    Task SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId);
    Task SetInitialVariables(ExpandoObject variables);
    [AlwaysInterleave]
    Task OnChildWorkflowCompleted(string parentActivityId, ExpandoObject childVariables);
    [AlwaysInterleave]
    Task OnChildWorkflowFailed(string parentActivityId, Exception exception);

    Task HandleMessageDelivery(string activityId, Guid hostActivityInstanceId, ExpandoObject variables);
    Task HandleBoundaryMessageFired(string boundaryActivityId, Guid hostActivityInstanceId);
    Task HandleTimerFired(string timerActivityId, Guid hostActivityInstanceId);
    [AlwaysInterleave]
    Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId);
    [AlwaysInterleave]
    Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId);
}
