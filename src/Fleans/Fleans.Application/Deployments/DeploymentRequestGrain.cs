using System.Text;
using Fleans.Application.Bpmn;
using Fleans.Application.Grains;
using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Deployments;

[CorePlacement]
public partial class DeploymentRequestGrain : Grain, IDeploymentRequestGrain
{
    private readonly IPersistentState<DeploymentRequestState> _state;
    private readonly IBpmnConverter _bpmnConverter;
    private readonly WorkflowExpressionValidator _expressionValidator;
    private readonly ILogger<DeploymentRequestGrain> _logger;

    public DeploymentRequestGrain(
        [PersistentState("state", GrainStorageNames.DeploymentRequests)]
        IPersistentState<DeploymentRequestState> state,
        IBpmnConverter bpmnConverter,
        WorkflowExpressionValidator expressionValidator,
        ILogger<DeploymentRequestGrain> logger)
    {
        _state = state;
        _bpmnConverter = bpmnConverter;
        _expressionValidator = expressionValidator;
        _logger = logger;
    }

    private DeploymentRequestState Request => _state.State;

    public async Task<DeploymentRequestState> Submit(string bpmnXml, string requestedBy, string? comment,
        int requiredApprovals, bool deployDisabled, List<string> reviewerGroups)
    {
        var workflow = await Parse(bpmnXml);
        // Rejected before review: reviewers should not approve a process that cannot deploy.
        _expressionValidator.Validate(workflow);
        Request.Submit(this.GetPrimaryKey(), workflow.WorkflowId, bpmnXml, requestedBy, comment,
            requiredApprovals, deployDisabled, DateTimeOffset.UtcNow, reviewerGroups);
        await _state.WriteStateAsync();
        LogSubmitted(Request.Id, Request.ProcessDefinitionKey, requestedBy, requiredApprovals);

        if (Request.Status == DeploymentRequestStatus.Approved)
            await Deploy();
        return Request;
    }

    public async Task<DeploymentRequestState> Approve(string userId, List<string> userGroups, string? comment)
    {
        Request.Approve(userId, userGroups, comment, DateTimeOffset.UtcNow);
        // Persisted before the deploy: a failed deploy must not lose the approval.
        await _state.WriteStateAsync();
        LogApproved(Request.Id, userId, Request.Approvals.Count, Request.RequiredApprovals);

        if (Request.Status == DeploymentRequestStatus.Approved)
            await Deploy();
        return Request;
    }

    public async Task<DeploymentRequestState> Reject(string userId, List<string> userGroups, string reason)
    {
        Request.Reject(userId, userGroups, reason, DateTimeOffset.UtcNow);
        await _state.WriteStateAsync();
        LogRejected(Request.Id, userId);
        return Request;
    }

    public async Task<DeploymentRequestState> Withdraw(string userId)
    {
        Request.Withdraw(userId, DateTimeOffset.UtcNow);
        await _state.WriteStateAsync();
        LogWithdrawn(Request.Id, userId);
        return Request;
    }

    public async Task<DeploymentRequestState> RetryDeployment()
    {
        if (Request.Status != DeploymentRequestStatus.Approved)
            throw new InvalidOperationException($"Only an approved request can be deployed; this one is {Request.Status}.");

        await Deploy();
        return Request;
    }

    public Task<DeploymentRequestState?> GetState() =>
        Task.FromResult(_state.RecordExists ? Request : null);

    /// <summary>
    /// Deploys the approved request and records the outcome. A deploy failure is recorded, not
    /// thrown: the request stays Approved with <see cref="DeploymentRequestState.DeployError"/>.
    /// </summary>
    private async Task Deploy()
    {
        var processGrain = GrainFactory.GetGrain<IProcessDefinitionGrain>(Request.ProcessDefinitionKey);
        try
        {
            var workflow = await Parse(Request.BpmnXml);
            var deployed = await processGrain.DeployVersion(workflow, Request.BpmnXml);
            Request.MarkDeployed(deployed.ProcessDefinitionId, deployed.Version, DateTimeOffset.UtcNow);
            LogDeployed(Request.Id, deployed.ProcessDefinitionId, deployed.Version);
        }
        catch (Exception ex)
        {
            Request.MarkDeployFailed(ex.Message);
            LogDeployFailed(Request.Id, ex);
        }
        await _state.WriteStateAsync();

        // Same order as the editor's "deploy disabled": deploy, then disable the process.
        if (Request is { Status: DeploymentRequestStatus.Deployed, DeployDisabled: true })
            await processGrain.Disable();
    }

    private async Task<WorkflowDefinition> Parse(string bpmnXml)
    {
        if (string.IsNullOrWhiteSpace(bpmnXml))
            throw new ArgumentException("BpmnXml is required.", nameof(bpmnXml));
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml));
            return await _bpmnConverter.ConvertFromXmlAsync(stream);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException($"Invalid BPMN: {ex.Message}", nameof(bpmnXml), ex);
        }
    }

    [LoggerMessage(EventId = 14300, Level = LogLevel.Information,
        Message = "Deployment request {RequestId} submitted for process {ProcessDefinitionKey} by {RequestedBy}; {RequiredApprovals} approval(s) required")]
    private partial void LogSubmitted(Guid requestId, string processDefinitionKey, string requestedBy, int requiredApprovals);

    [LoggerMessage(EventId = 14301, Level = LogLevel.Information,
        Message = "Deployment request {RequestId} approved by {UserId} ({ApprovalCount} of {RequiredApprovals})")]
    private partial void LogApproved(Guid requestId, string userId, int approvalCount, int requiredApprovals);

    [LoggerMessage(EventId = 14302, Level = LogLevel.Information,
        Message = "Deployment request {RequestId} rejected by {UserId}")]
    private partial void LogRejected(Guid requestId, string userId);

    [LoggerMessage(EventId = 14303, Level = LogLevel.Information,
        Message = "Deployment request {RequestId} withdrawn by {UserId}")]
    private partial void LogWithdrawn(Guid requestId, string userId);

    [LoggerMessage(EventId = 14304, Level = LogLevel.Information,
        Message = "Deployment request {RequestId} deployed as {ProcessDefinitionId} (v{Version})")]
    private partial void LogDeployed(Guid requestId, string processDefinitionId, int version);

    [LoggerMessage(EventId = 14305, Level = LogLevel.Warning,
        Message = "Deployment request {RequestId} is approved but its deploy failed; it can be retried")]
    private partial void LogDeployFailed(Guid requestId, Exception exception);
}
