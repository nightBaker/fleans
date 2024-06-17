using Fleans.Domain.Events;
using Fleans.Domain.States;
using Orleans;

namespace Fleans.Domain;

public interface IWorkflowInstance : IGrainWithGuidKey
{
    ValueTask<Guid> GetWorkflowInstanceId();
    ValueTask<IWorkflowInstanceState> GetState();
    ValueTask<IWorkflowDefinition> GetWorkflowDefinition();

    Task CompleteActivity(string activityId, Dictionary<string, object> variables, IEventPublisher eventPublisher);
    Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result);
    Task FailActivity(string activityId, Exception exception, IEventPublisher eventPublisher);
    Task StartWorkflow();        
    Task SetWorkflow(IWorkflowDefinition workflow);
    void EnqueueEvent(IDomainEvent domainEvent);
    void Start();
    void Complete();
}