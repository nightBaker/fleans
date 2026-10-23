using System.Diagnostics.CodeAnalysis;
using Fleans.Api.Authorization;
using Fleans.Application;
using Fleans.Application.QueryModels;
using Fleans.Domain.States;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fleans.Api.Controllers
{
    /// <summary>
    /// Deployment requests (#900): a deploy that needs <c>Fleans:Deployments:RequiredApprovals</c>
    /// approvals from distinct reviewers, never the requester. With 0 required, submitting deploys
    /// at once — which is how another environment's Admin UI promotes a version here. Rule
    /// violations surface through <see cref="GlobalExceptionHandler"/>: 400 bad input, 404 unknown
    /// request, 409 a transition the request's status or the four-eyes rule forbids.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public partial class DeploymentsController : ControllerBase
    {
        private readonly ILogger<DeploymentsController> _logger;
        private readonly IWorkflowCommandService _commandService;
        private readonly IWorkflowQueryService _queryService;
        private readonly IUserIdResolver _userIdResolver;

        public DeploymentsController(
            ILogger<DeploymentsController> logger,
            IWorkflowCommandService commandService,
            IWorkflowQueryService queryService,
            IUserIdResolver userIdResolver)
        {
            _logger = logger;
            _commandService = commandService;
            _queryService = queryService;
            _userIdResolver = userIdResolver;
        }

        [EnableRateLimiting("workflow-mutation")]
        [HttpPost("requests", Name = "SubmitDeploymentRequest")]
        public async Task<IActionResult> Submit([FromBody] SubmitDeploymentRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.BpmnXml))
                return BadRequest(new ErrorResponse("BpmnXml is required."));
            if (!TryResolveUserId(request.RequestedBy, out var requestedBy, out var rejection))
                return rejection;

            var submitted = await _commandService.SubmitDeployment(
                request.BpmnXml, requestedBy, request.Comment, request.DeployDisabled);
            return CreatedAtRoute("GetDeploymentRequest", new { requestId = submitted.Id }, ToDto(submitted));
        }

        [EnableRateLimiting("read")]
        [HttpGet("requests", Name = "ListDeploymentRequests")]
        public async Task<IActionResult> List([FromQuery] DeploymentRequestStatus? status = null, [FromQuery] int limit = 100)
        {
            var requests = await _queryService.GetDeploymentRequests(status, Math.Clamp(limit, 1, 500));
            return Ok(requests.Select(ToDto).ToList());
        }

        [EnableRateLimiting("read")]
        [HttpGet("requests/{requestId:guid}", Name = "GetDeploymentRequest")]
        public async Task<IActionResult> Get(Guid requestId)
        {
            var request = await _queryService.GetDeploymentRequest(requestId);
            if (request is null)
                return NotFound(new ErrorResponse($"Deployment request '{requestId}' not found"));
            return Ok(ToDto(request));
        }

        /// <summary>The submitted BPMN XML, for reviewing what will be deployed.</summary>
        [EnableRateLimiting("read")]
        [HttpGet("requests/{requestId:guid}/bpmn", Name = "GetDeploymentRequestBpmn")]
        public async Task<IActionResult> GetBpmn(Guid requestId)
        {
            var request = await _queryService.GetDeploymentRequest(requestId);
            if (request is null)
                return NotFound(new ErrorResponse($"Deployment request '{requestId}' not found"));
            return Content(request.BpmnXml, "application/xml");
        }

        [EnableRateLimiting("admin")]
        [HttpPost("requests/{requestId:guid}/approve", Name = "ApproveDeploymentRequest")]
        public async Task<IActionResult> Approve(Guid requestId, [FromBody] ApproveDeploymentRequest? request)
        {
            if (!TryResolveUserId(request?.UserId, out var userId, out var rejection))
                return rejection;

            var approved = await _commandService.ApproveDeployment(requestId, userId, request?.Comment);
            return Ok(ToDto(approved));
        }

        [EnableRateLimiting("admin")]
        [HttpPost("requests/{requestId:guid}/reject", Name = "RejectDeploymentRequest")]
        public async Task<IActionResult> Reject(Guid requestId, [FromBody] RejectDeploymentRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Reason))
                return BadRequest(new ErrorResponse("Reason is required."));
            if (!TryResolveUserId(request.UserId, out var userId, out var rejection))
                return rejection;

            var rejected = await _commandService.RejectDeployment(requestId, userId, request.Reason);
            return Ok(ToDto(rejected));
        }

        [EnableRateLimiting("admin")]
        [HttpPost("requests/{requestId:guid}/withdraw", Name = "WithdrawDeploymentRequest")]
        public async Task<IActionResult> Withdraw(Guid requestId, [FromBody] WithdrawDeploymentRequest? request)
        {
            if (!TryResolveUserId(request?.UserId, out var userId, out var rejection))
                return rejection;

            var withdrawn = await _commandService.WithdrawDeployment(requestId, userId);
            return Ok(ToDto(withdrawn));
        }

        /// <summary>Deploys an approved request whose last deploy attempt failed.</summary>
        [EnableRateLimiting("admin")]
        [HttpPost("requests/{requestId:guid}/retry", Name = "RetryDeploymentRequest")]
        public async Task<IActionResult> Retry(Guid requestId)
        {
            var retried = await _commandService.RetryDeployment(requestId);
            return Ok(ToDto(retried));
        }

        private bool TryResolveUserId(string? bodyUserId, out string userId,
            [NotNullWhen(false)] out IActionResult? rejection)
        {
            var resolution = _userIdResolver.Resolve(HttpContext, bodyUserId);
            userId = resolution.UserId ?? string.Empty;
            switch (resolution.Status)
            {
                case UserIdResolutionStatus.Resolved:
                    rejection = null;
                    return true;
                case UserIdResolutionStatus.Mismatch:
                    LogUserIdMismatch();
                    rejection = StatusCode(StatusCodes.Status403Forbidden,
                        new ErrorResponse("UserId does not match the authenticated user"));
                    return false;
                case UserIdResolutionStatus.NoIdentityClaim:
                    LogNoIdentityClaim();
                    rejection = StatusCode(StatusCodes.Status403Forbidden,
                        new ErrorResponse("Authenticated user has no user id claim"));
                    return false;
                default:
                    rejection = BadRequest(new ErrorResponse("UserId is required"));
                    return false;
            }
        }

        private static DeploymentRequestDto ToDto(DeploymentRequestState r) => new(
            r.Id, r.ProcessDefinitionKey, r.RequestedBy, r.RequestedAt, r.Comment, r.RequiredApprovals,
            ToDto(r.Approvals), r.Status.ToString(), r.DeployDisabled, r.ClosedBy, r.CloseReason,
            r.DeployedVersion, r.DeployError);

        private static DeploymentRequestDto ToDto(DeploymentRequestSummary r) => new(
            r.Id, r.ProcessDefinitionKey, r.RequestedBy, r.RequestedAt, r.Comment, r.RequiredApprovals,
            ToDto(r.Approvals), r.Status.ToString(), r.DeployDisabled, r.ClosedBy, r.CloseReason,
            r.DeployedVersion, r.DeployError);

        private static List<DeploymentApprovalDto> ToDto(List<DeploymentApproval> approvals) =>
            approvals.Select(a => new DeploymentApprovalDto(a.UserId, a.ApprovedAt, a.Comment)).ToList();

        [LoggerMessage(EventId = 8200, Level = LogLevel.Warning,
            Message = "Deployment request action rejected: body UserId does not match the authenticated user")]
        private partial void LogUserIdMismatch();

        [LoggerMessage(EventId = 8201, Level = LogLevel.Warning,
            Message = "Deployment request action rejected: the authenticated user has no user id claim")]
        private partial void LogNoIdentityClaim();
    }
}
