using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Microsoft.Extensions.Logging;
using System.Dynamic;

namespace Fleans.Application;

public partial class WorkflowCommandService : IWorkflowCommandService
{
    private const int WorkflowInstanceFactorySingletonId = 0;

    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowCommandService> _logger;

    public WorkflowCommandService(IGrainFactory grainFactory, ILogger<WorkflowCommandService> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async Task<Guid> StartWorkflow(string workflowId)
    {
        LogStartingWorkflow(workflowId);

        var instanceId = await _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId)
                                            .CreateWorkflowInstanceGrain(workflowId);

        await _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId).StartWorkflow();

        return instanceId;
    }

    public async Task<Guid> StartWorkflowByProcessDefinitionId(string processDefinitionId)
    {
        LogStartingWorkflowByDefinition(processDefinitionId);

        var instanceId = await _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId)
            .CreateWorkflowInstanceGrainByProcessDefinitionId(processDefinitionId);

        await _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId).StartWorkflow();

        return instanceId;
    }

    public void CompleteActivity(Guid workflowInstanceId, string activityId, ExpandoObject variables)
    {
        LogCompletingActivity(workflowInstanceId, activityId);

        _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId)
                     .CompleteActivity(activityId, variables);
    }

    public async Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml)
    {
        LogDeployingWorkflow(workflow.WorkflowId);

        var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
        return await factoryGrain.DeployWorkflow(workflow, bpmnXml);
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
