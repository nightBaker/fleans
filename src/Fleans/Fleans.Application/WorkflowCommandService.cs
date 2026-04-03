using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Microsoft.Extensions.Logging;
using System.Dynamic;

namespace Fleans.Application;

public partial class WorkflowCommandService : IWorkflowCommandService
{
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

        var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(workflowId);
        var workflowInstance = await processGrain.CreateInstance();

        await workflowInstance.StartWorkflow();

        return await workflowInstance.GetWorkflowInstanceId();
    }

    public async Task<Guid> StartWorkflowByProcessDefinitionId(string processDefinitionId)
    {
        LogStartingWorkflowByDefinition(processDefinitionId);

        var key = ProcessDefinition.ExtractKeyFromId(processDefinitionId);
        var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(key);
        var workflowInstance = await processGrain.CreateInstanceByDefinitionId(processDefinitionId);

        await workflowInstance.StartWorkflow();

        return await workflowInstance.GetWorkflowInstanceId();
    }

    public async Task CompleteActivity(Guid workflowInstanceId, string activityId, ExpandoObject variables)
    {
        LogCompletingActivity(workflowInstanceId, activityId);

        await _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId)
                     .CompleteActivity(activityId, variables);
    }

    public async Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml)
    {
        LogDeployingWorkflow(workflow.WorkflowId);

        var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId);
        return await processGrain.DeployVersion(workflow, bpmnXml);
    }

    [LoggerMessage(EventId = 7000, Level = LogLevel.Information, Message = "Starting workflow {WorkflowId}")]
    private partial void LogStartingWorkflow(string workflowId);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "Starting workflow by process definition {ProcessDefinitionId}")]
    private partial void LogStartingWorkflowByDefinition(string processDefinitionId);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Information, Message = "Completing activity {ActivityId} for workflow instance {WorkflowInstanceId}")]
    private partial void LogCompletingActivity(Guid workflowInstanceId, string activityId);

    public async Task<SendMessageResult> SendMessage(string messageName, string? correlationKey, ExpandoObject variables)
    {
        LogSendingMessage(messageName, correlationKey);

        // Try correlation-based delivery first if a correlation key is provided
        if (!string.IsNullOrWhiteSpace(correlationKey))
        {
            var grainKey = MessageCorrelationKey.Build(messageName, correlationKey);
            var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
            var delivered = await correlationGrain.DeliverMessage(variables);

            if (delivered)
                return new SendMessageResult(Delivered: true);
        }

        // Fallthrough: try message start event listener
        var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(messageName);
        var instanceIds = await listener.FireMessageStartEvent(variables);

        if (instanceIds.Count > 0)
            return new SendMessageResult(Delivered: true, WorkflowInstanceIds: instanceIds);

        return new SendMessageResult(Delivered: false);
    }

    [LoggerMessage(EventId = 7003, Level = LogLevel.Information, Message = "Deploying workflow {WorkflowId}")]
    private partial void LogDeployingWorkflow(string workflowId);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Information, Message = "Sending message '{MessageName}' with correlation key '{CorrelationKey}'")]
    private partial void LogSendingMessage(string messageName, string? correlationKey);

    public async Task<SendSignalResult> SendSignal(string signalName)
    {
        LogSendingSignal(signalName);

        int deliveredCount = 0;
        List<Guid>? instanceIds = null;
        List<string>? errors = null;

        // Fan-out: broadcast to running instances AND create new instances
        // Both always execute independently
        try
        {
            var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalName);
            deliveredCount = await signalGrain.BroadcastSignal();
        }
        catch (Exception ex)
        {
            LogSignalBroadcastFailed(signalName, ex);
            errors ??= [];
            errors.Add($"Broadcast failed: {ex.Message}");
        }

        try
        {
            var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(signalName);
            instanceIds = await listener.FireSignalStartEvent();
            if (instanceIds.Count == 0)
                instanceIds = null;
        }
        catch (Exception ex)
        {
            LogSignalStartEventFireFailed(signalName, ex);
            errors ??= [];
            errors.Add($"Start event failed: {ex.Message}");
        }

        return new SendSignalResult(deliveredCount, instanceIds, errors);
    }

    [LoggerMessage(EventId = 7005, Level = LogLevel.Information, Message = "Sending signal '{SignalName}'")]
    private partial void LogSendingSignal(string signalName);

    [LoggerMessage(EventId = 7006, Level = LogLevel.Error, Message = "Failed to broadcast signal '{SignalName}' to running instances")]
    private partial void LogSignalBroadcastFailed(string signalName, Exception ex);

    [LoggerMessage(EventId = 7007, Level = LogLevel.Error, Message = "Failed to fire signal start event for '{SignalName}'")]
    private partial void LogSignalStartEventFireFailed(string signalName, Exception ex);

    public async Task ClaimUserTask(Guid workflowInstanceId, Guid activityInstanceId, string userId)
    {
        LogClaimingUserTask(workflowInstanceId, activityInstanceId, userId);
        var grain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId);
        await grain.ClaimUserTask(activityInstanceId, userId);
    }

    public async Task UnclaimUserTask(Guid workflowInstanceId, Guid activityInstanceId)
    {
        LogUnclaimingUserTask(workflowInstanceId, activityInstanceId);
        var grain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId);
        await grain.UnclaimUserTask(activityInstanceId);
    }

    public async Task CompleteUserTask(Guid workflowInstanceId, Guid activityInstanceId, string userId, ExpandoObject variables)
    {
        LogCompletingUserTask(workflowInstanceId, activityInstanceId, userId);
        var grain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId);
        await grain.CompleteUserTask(activityInstanceId, userId, variables);
    }

    public async Task<ProcessDefinitionSummary> DisableProcess(string processDefinitionKey)
    {
        LogDisablingProcess(processDefinitionKey);
        var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(processDefinitionKey);
        return await processGrain.Disable();
    }

    public async Task<ProcessDefinitionSummary> EnableProcess(string processDefinitionKey)
    {
        LogEnablingProcess(processDefinitionKey);
        var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(processDefinitionKey);
        return await processGrain.Enable();
    }

    [LoggerMessage(EventId = 7008, Level = LogLevel.Information,
        Message = "Claiming user task: WorkflowInstanceId={WorkflowInstanceId}, ActivityInstanceId={ActivityInstanceId}, UserId={UserId}")]
    private partial void LogClaimingUserTask(Guid workflowInstanceId, Guid activityInstanceId, string userId);

    [LoggerMessage(EventId = 7009, Level = LogLevel.Information,
        Message = "Unclaiming user task: WorkflowInstanceId={WorkflowInstanceId}, ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogUnclaimingUserTask(Guid workflowInstanceId, Guid activityInstanceId);

    [LoggerMessage(EventId = 7010, Level = LogLevel.Information,
        Message = "Completing user task: WorkflowInstanceId={WorkflowInstanceId}, ActivityInstanceId={ActivityInstanceId}, UserId={UserId}")]
    private partial void LogCompletingUserTask(Guid workflowInstanceId, Guid activityInstanceId, string userId);

    [LoggerMessage(EventId = 7011, Level = LogLevel.Information, Message = "Disabling process {ProcessDefinitionKey}")]
    private partial void LogDisablingProcess(string processDefinitionKey);

    [LoggerMessage(EventId = 7012, Level = LogLevel.Information, Message = "Enabling process {ProcessDefinitionKey}")]
    private partial void LogEnablingProcess(string processDefinitionKey);
}
