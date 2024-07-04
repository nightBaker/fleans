using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fleans.Application.Events;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;

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
            //    var workflowInstance = await _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstaceFactorySingletonId)
            //                                            .CreateWorkflowInstanceGrain(workflowId);

            //    await workflowInstance.StartWorkflow();

            //    return workflowInstance.GetPrimaryKey();

            var testWF = _grainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
                        
            await testWF.SetWorkflow(CreateSimpleWorkflowWithExclusiveGateway());
            await testWF.StartWorkflow();

            return testWF.GetPrimaryKey();
    }

        public void CompleteActivity(Guid workflowInstanceId, string activityId, Dictionary<string, object> variables)
        {            
            _grainFactory.GetGrain<IWorkflowInstance>(workflowInstanceId)
                         .CompleteActivity(activityId, variables);

        }

        private static IWorkflowDefinition CreateSimpleWorkflowWithExclusiveGateway()
        {
            var start = new StartEvent("start");
            var end1 = new EndEvent("end1");
            var end2 = new EndEvent("end2");
            var ifActivity = new ExclusiveGateway("if");

            var workflow = new WorkflowDefinition { WorkflowId = "workflow1", Activities = new List<Domain.Activities.Activity>(), SequenceFlows = new List<SequenceFlow>() };
            workflow.Activities.Add(start);
            workflow.Activities.Add(end1);
            workflow.Activities.Add(end2);
            workflow.Activities.Add(ifActivity);

            workflow.SequenceFlows.Add(new SequenceFlow("seq1", start, ifActivity));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow("seq2", ifActivity, end1, "trueCondition"));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow("seq3", ifActivity, end2, "falseCondition"));
            return workflow;
        }
    }
}
