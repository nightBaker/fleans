using Fleans.Domain.Events;
using Fleans.Domain.States;
using Orleans;
using Orleans.Concurrency;
using System.Dynamic;

namespace Fleans.Domain;

public interface IWorkflowInstance : IGrainWithGuidKey
{
    [ReadOnly]
    ValueTask<Guid> GetWorkflowInstanceId();
    [ReadOnly]
    ValueTask<IWorkflowInstanceState> GetState();
    [ReadOnly]
    ValueTask<IWorkflowDefinition> GetWorkflowDefinition();

    Task CompleteActivity(string activityId, ExpandoObject variables);
    Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result);
    Task FailActivity(string activityId, Exception exception);
    Task StartWorkflow();        
    Task SetWorkflow(IWorkflowDefinition workflow);
    void EnqueueEvent(IDomainEvent domainEvent);
    ValueTask Start();
    ValueTask Complete();

    [ReadOnly]
    ValueTask<ExpandoObject> GetVariables(Guid variablesStateId);
}