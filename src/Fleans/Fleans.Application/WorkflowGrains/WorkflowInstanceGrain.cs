using Fleans.Application.Events;
using Fleans.Domain;
using Fleans.Domain.Events;
using System;

namespace Fleans.Application.WorkflowGrains;

public class WorkflowInstanceGrain : Grain, IWorkflowInstanceGrain
{
    private const int SingletonEventPublisherGrainId = 0;
    private WorkflowInstance? _workflowInstance;
    private readonly IGrainFactory _grainFactory;

    public WorkflowInstanceGrain(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    private WorkflowInstance WorkflowInstance
        => _workflowInstance ?? throw new InvalidOperationException("Workflow not set");

    public void SetWorkflow(Workflow workflow)
    {
        _workflowInstance = new WorkflowInstance(this.GetPrimaryKey(), workflow: workflow);
    }

    public void StartWorkflow()
    {
        var eventsPublisherGrain = _grainFactory.GetGrain<IWorkflowEventsPublisher>(SingletonEventPublisherGrainId);

        WorkflowInstance.StartWorkflow(eventsPublisherGrain);
    }

    public void CompleteActivity(string activityId, Dictionary<string, object> variables)
    {
        var eventsPublisherGrain = _grainFactory.GetGrain<IWorkflowEventsPublisher>(SingletonEventPublisherGrainId);

        WorkflowInstance.CompleteActivity(activityId, variables, eventsPublisherGrain);
    }
}
