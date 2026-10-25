using Fleans.Application.Deployments;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Application.StartEvents;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Dynamic;

namespace Fleans.Application;

public partial class WorkflowCommandService : IWorkflowCommandService
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowCommandService> _logger;
    private readonly DeploymentApprovalOptions _deploymentApprovals;
    private readonly SignalCorrelationOptions _signals;
    private readonly StartEventLauncher _startEvents;

    public WorkflowCommandService(IGrainFactory grainFactory, ILogger<WorkflowCommandService> logger,
        IOptions<DeploymentApprovalOptions> deploymentApprovals, IOptions<SignalCorrelationOptions> signals,
        StartEventLauncher startEvents)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _deploymentApprovals = deploymentApprovals.Value;
        _signals = signals.Value;
        _startEvents = startEvents;
    }

    public async Task<Guid> StartWorkflow(string workflowId, ExpandoObject? initialVariables = null, string? businessId = null)
    {
        LogStartingWorkflow(workflowId);
        // Fail fast: a bad id is a 400 before any definition lookup (404/409) or grain activation.
        BusinessIdRules.Validate(businessId);

        var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(workflowId);
        var definition = await processGrain.GetStartableDefinition()
            ?? throw new ProcessDisabledException(workflowId);

        return await StartInstance(definition, initialVariables, businessId);
    }

    public async Task<Guid> StartWorkflowByProcessDefinitionId(string processDefinitionId, string? businessId = null)
    {
        LogStartingWorkflowByDefinition(processDefinitionId);
        // Fail fast: a bad id is a 400 before any definition lookup (404/409) or grain activation.
        BusinessIdRules.Validate(businessId);

        var key = ProcessDefinition.ExtractKeyFromId(processDefinitionId);
        var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(key);
        var definition = await processGrain.GetStartableDefinition(processDefinitionId)
            ?? throw new ProcessDisabledException(key);

        return await StartInstance(definition, initialVariables: null, businessId);
    }

    /// <summary>One grain call and one journal append per start (#863).</summary>
    private async Task<Guid> StartInstance(IWorkflowDefinition definition, ExpandoObject? initialVariables, string? businessId)
    {
        // A random (v4) id on purpose: it is the journal's shard key, and a time-ordered id would
        // put every new instance on the same shard (docs/conventions/invariants.md, #879).
        var instanceId = Guid.NewGuid();
        await _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId)
            .StartWorkflow(definition, businessId: businessId, variables: initialVariables);
        return instanceId;
    }

    public async Task CompleteActivity(Guid workflowInstanceId, string activityId, ExpandoObject variables)
    {
        LogCompletingActivity(workflowInstanceId, activityId);

        await _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId)
                     .CompleteActivity(activityId, variables);
    }

    public async Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml)
    {
        if (_deploymentApprovals.RequiredApprovals > 0)
            throw new DeploymentApprovalRequiredException(_deploymentApprovals.RequiredApprovals);

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

    public async Task<SendMessageResult> SendMessage(string messageName, string? correlationKey, ExpandoObject variables, string? businessId = null)
    {
        LogSendingMessage(messageName, correlationKey);
        // Validated here too: a correlated delivery below never reaches a start event.
        BusinessIdRules.Validate(businessId);

        // Try correlation-based delivery first if a correlation key is provided. A key over the
        // length limit can have no subscriber, so it goes straight to the start-event path.
        var grainKey = string.IsNullOrWhiteSpace(correlationKey)
            ? null
            : MessageCorrelationKey.TryBuild(messageName, correlationKey);
        if (grainKey is not null)
        {
            var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
            var delivered = await correlationGrain.DeliverMessage(variables);

            if (delivered)
                return new SendMessageResult(Delivered: true);
        }

        // Fallthrough: message start events. businessId applies only to instances they create (#824).
        var instanceIds = await _startEvents.StartByMessage(messageName, variables, businessId);

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
        // Fans out to every bucket; the buckets that answered still count when some fail.
        var broadcast = await _grainFactory.BroadcastSignal(signalName, _signals);
        deliveredCount = broadcast.DeliveredCount;
        if (broadcast.Failure is not null)
        {
            LogSignalBroadcastFailed(signalName, broadcast.Failure);
            errors ??= [];
            errors.Add($"Broadcast failed in {broadcast.FailedBucketCount} of {_signals.BucketCount} buckets: "
                + broadcast.Failure.InnerExceptions[0].Message);
        }

        try
        {
            instanceIds = await _startEvents.StartBySignal(signalName);
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

    public async Task ClaimUserTask(Guid workflowInstanceId, Guid activityInstanceId, string userId, IReadOnlyList<string> userGroups)
    {
        LogClaimingUserTask(workflowInstanceId, activityInstanceId, userId);
        var grain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId);
        await grain.ClaimUserTask(activityInstanceId, userId, userGroups);
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

    public async Task FailUserTask(Guid workflowInstanceId, Guid activityInstanceId, string errorCode, string errorMessage)
    {
        LogFailingUserTask(workflowInstanceId, activityInstanceId, errorCode);
        var grain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId);
        await grain.FailUserTask(activityInstanceId, errorCode, errorMessage);
    }

    public async Task CancelUserTask(Guid workflowInstanceId, Guid activityInstanceId, string? reason)
    {
        LogCancellingUserTask(workflowInstanceId, activityInstanceId);
        var grain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId);
        await grain.CancelUserTask(activityInstanceId, reason);
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

    [LoggerMessage(EventId = 7015, Level = LogLevel.Information,
        Message = "Failing user task: WorkflowInstanceId={WorkflowInstanceId}, ActivityInstanceId={ActivityInstanceId}, ErrorCode={ErrorCode}")]
    private partial void LogFailingUserTask(Guid workflowInstanceId, Guid activityInstanceId, string errorCode);

    [LoggerMessage(EventId = 7016, Level = LogLevel.Information,
        Message = "Cancelling user task: WorkflowInstanceId={WorkflowInstanceId}, ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogCancellingUserTask(Guid workflowInstanceId, Guid activityInstanceId);

    public async Task<EvaluateConditionsResult> EvaluateConditions(string? workflowId, ExpandoObject variables)
    {
        LogEvaluatingConditions(workflowId);
        return await _startEvents.StartByConditions(workflowId, variables);
    }

    [LoggerMessage(EventId = 7013, Level = LogLevel.Information, Message = "Evaluating conditional start events (workflowId={WorkflowId})")]
    private partial void LogEvaluatingConditions(string? workflowId);

    public async Task<DeploymentRequestState> SubmitDeployment(string bpmnXml, string requestedBy, string? comment, bool deployDisabled)
    {
        var requestId = Guid.NewGuid();
        LogSubmittingDeployment(requestId, requestedBy);
        return await _grainFactory.GetGrain<IDeploymentRequestGrain>(requestId)
            .Submit(bpmnXml, requestedBy, comment, _deploymentApprovals.RequiredApprovals, deployDisabled,
                _deploymentApprovals.ReviewerGroups.ToList());
    }

    public Task<DeploymentRequestState> ApproveDeployment(Guid requestId, string userId, IReadOnlyList<string> userGroups, string? comment) =>
        _grainFactory.GetGrain<IDeploymentRequestGrain>(requestId).Approve(userId, userGroups.ToList(), comment);

    public Task<DeploymentRequestState> RejectDeployment(Guid requestId, string userId, IReadOnlyList<string> userGroups, string reason) =>
        _grainFactory.GetGrain<IDeploymentRequestGrain>(requestId).Reject(userId, userGroups.ToList(), reason);

    public Task<DeploymentRequestState> WithdrawDeployment(Guid requestId, string userId) =>
        _grainFactory.GetGrain<IDeploymentRequestGrain>(requestId).Withdraw(userId);

    public Task<DeploymentRequestState> RetryDeployment(Guid requestId) =>
        _grainFactory.GetGrain<IDeploymentRequestGrain>(requestId).RetryDeployment();

    [LoggerMessage(EventId = 7011, Level = LogLevel.Information, Message = "Disabling process {ProcessDefinitionKey}")]
    private partial void LogDisablingProcess(string processDefinitionKey);

    [LoggerMessage(EventId = 7012, Level = LogLevel.Information, Message = "Enabling process {ProcessDefinitionKey}")]
    private partial void LogEnablingProcess(string processDefinitionKey);

    [LoggerMessage(EventId = 7020, Level = LogLevel.Information, Message = "Submitting deployment request {RequestId} by {RequestedBy}")]
    private partial void LogSubmittingDeployment(Guid requestId, string requestedBy);
}
