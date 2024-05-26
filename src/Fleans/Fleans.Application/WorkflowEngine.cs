using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fleans.Application
{
    public class WorkflowEngine
    {
        private const int WorkflowInstaceFactorySingletonId = 0;
        private readonly IGrainFactory _grainFactory;

        public WorkflowEngine(IGrainFactory grainFactory)
        {
            _grainFactory = grainFactory;
        }

        public async Task StartWorkflow(string workflowId)
        {
            var workflowInstance = await _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstaceFactorySingletonId)
                                                    .CreateWorkflowInstance(workflowId);
            workflowInstance.StartWorkflow();
        }

        public void CompleteActivity(Guid workflowInstanceId, string activityId, Dictionary<string, object> variables)
        {

           _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId)
                        .CompleteActivity(activityId, variables);
            
        }
    }
}
