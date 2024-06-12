using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fleans.Application.Events;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;

namespace Fleans.Application
{
    public class WorkflowEngine
    {
        private const int WorkflowInstaceFactorySingletonId = 0;
        private const int SingletonEventPublisherGrainId = 0;

        private readonly IGrainFactory _grainFactory;

        public WorkflowEngine(IGrainFactory grainFactory)
        {
            _grainFactory = grainFactory;
        }

        public async Task<Guid> StartWorkflow(string workflowId)
        {
            var workflowInstance = await _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstaceFactorySingletonId)
                                                    .CreateWorkflowInstanceGrain(workflowId);

            var eventsPublisherGrain = _grainFactory.GetGrain<IWorkflowEventsPublisher>(SingletonEventPublisherGrainId);

            workflowInstance.StartWorkflow(eventsPublisherGrain);

            return workflowInstance.GetPrimaryKey();
        }

        public void CompleteActivity(Guid workflowInstanceId, string activityId, Dictionary<string, object> variables)
        {

            var eventsPublisherGrain = _grainFactory.GetGrain<IWorkflowEventsPublisher>(SingletonEventPublisherGrainId);

            _grainFactory.GetGrain<IWorkflowInstance>(workflowInstanceId)
                         .CompleteActivity(activityId, variables, eventsPublisherGrain);

        }
    }
}
