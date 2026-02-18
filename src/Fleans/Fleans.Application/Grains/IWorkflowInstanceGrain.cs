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
    new ValueTask<IWorkflowDefinition> GetWorkflowDefinition();

    [ReadOnly]
    new ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();

    [ReadOnly]
    new ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();

    [ReadOnly]
    new ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();

    Task CompleteActivity(string activityId, ExpandoObject variables);
    Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result);
    Task FailActivity(string activityId, Exception exception);
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

    Task HandleBoundaryMessageFired(string boundaryActivityId);
}
