using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fleans.Application.Events;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.Logging;

namespace Fleans.Application
{
    public partial class WorkflowEngine
    {
        private const int WorkflowInstanceFactorySingletonId = 0;
        private const int SingletonEventPublisherGrainId = 0;

        private readonly IGrainFactory _grainFactory;
        private readonly ILogger<WorkflowEngine> _logger;

        public WorkflowEngine(IGrainFactory grainFactory, ILogger<WorkflowEngine> logger)
        {
            _grainFactory = grainFactory;
            _logger = logger;
        }

        public async Task<Guid> StartWorkflow(string workflowId)
        {
            LogStartingWorkflow(workflowId);

            var workflowInstance = await _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId)
                                                    .CreateWorkflowInstanceGrain(workflowId);

            await workflowInstance.StartWorkflow();

            return await workflowInstance.GetWorkflowInstanceId();
        }

        public async Task<Guid> StartWorkflowByProcessDefinitionId(string processDefinitionId)
        {
            LogStartingWorkflowByDefinition(processDefinitionId);

            var workflowInstance = await _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId)
                .CreateWorkflowInstanceGrainByProcessDefinitionId(processDefinitionId);

            await workflowInstance.StartWorkflow();

            return await workflowInstance.GetWorkflowInstanceId();
        }

        public void CompleteActivity(Guid workflowInstanceId, string activityId, ExpandoObject variables)
        {
            LogCompletingActivity(workflowInstanceId, activityId);

            _grainFactory.GetGrain<IWorkflowInstance>(workflowInstanceId)
                         .CompleteActivity(activityId, variables);

        }

        public async Task RegisterWorkflow(IWorkflowDefinition workflow)
        {
            var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
            await factoryGrain.RegisterWorkflow(workflow);
        }

        public async Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml)
        {
            LogDeployingWorkflow(workflow.WorkflowId);

            var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
            return await factoryGrain.DeployWorkflow(workflow, bpmnXml);
        }

        public async Task<IReadOnlyList<WorkflowSummary>> GetAllWorkflows()
        {
            var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
            var workflows = await factoryGrain.GetAllWorkflows();

            return workflows.Select(wf => new WorkflowSummary(wf.WorkflowId, wf.Activities.Count, wf.SequenceFlows.Count))
                .ToList().AsReadOnly();
        }

        public async Task<IReadOnlyList<ProcessDefinitionSummary>> GetAllProcessDefinitions()
        {
            var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
            return await factoryGrain.GetAllProcessDefinitions();
        }

        public async Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey)
        {
            var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
            return await factoryGrain.GetInstancesByKey(processDefinitionKey);
        }

        public async Task<InstanceStateSnapshot> GetInstanceDetail(Guid instanceId)
        {
            var instance = _grainFactory.GetGrain<IWorkflowInstance>(instanceId);
            var state = await instance.GetState();
            return await state.GetStateSnapshot();
        }

        public async Task<string> GetBpmnXmlByKey(string processDefinitionKey)
        {
            var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
            return await factoryGrain.GetBpmnXmlByKey(processDefinitionKey);
        }

        public async Task<string> GetBpmnXml(Guid instanceId)
        {
            var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
            return await factoryGrain.GetBpmnXmlByInstanceId(instanceId);
        }

        private static IWorkflowDefinition CreateSimpleWorkflowWithExclusiveGateway()
        {
            var start = new StartEvent("start");
            var end1 = new EndEvent("end1");
            var end2 = new EndEvent("end2");
            var ifActivity = new ExclusiveGateway("if");
            var taskActivity = new TaskActivity("task");


            var workflow = new WorkflowDefinition { WorkflowId = "workflow1", Activities = new List<Domain.Activities.Activity>(), SequenceFlows = new List<SequenceFlow>() };
            workflow.Activities.Add(start);
            workflow.Activities.Add(taskActivity);
            workflow.Activities.Add(end1);
            workflow.Activities.Add(end2);
            workflow.Activities.Add(ifActivity);

            workflow.SequenceFlows.Add(new SequenceFlow("seq1", start, taskActivity));
            workflow.SequenceFlows.Add(new SequenceFlow("seq1.1", taskActivity, ifActivity));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow("seq2", ifActivity, end1, "_context.x > _context.y"));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow("seq3", ifActivity, end2, "_context.x < _context.y"));
            return workflow;
        }

        [LoggerMessage(EventId = 7000, Level = LogLevel.Information, Message = "Starting workflow {WorkflowId}")]
        private partial void LogStartingWorkflow(string workflowId);

        [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "Starting workflow by process definition {ProcessDefinitionId}")]
        private partial void LogStartingWorkflowByDefinition(string processDefinitionId);

        [LoggerMessage(EventId = 7002, Level = LogLevel.Information, Message = "Completing activity {ActivityId} for workflow instance {WorkflowInstanceId}")]
        private partial void LogCompletingActivity(Guid workflowInstanceId, string activityId);

        [LoggerMessage(EventId = 7003, Level = LogLevel.Information, Message = "Deploying workflow {WorkflowId}")]
        private partial void LogDeployingWorkflow(string workflowId);
    }
}
