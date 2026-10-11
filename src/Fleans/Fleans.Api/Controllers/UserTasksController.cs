using System.Diagnostics.CodeAnalysis;
using Fleans.Api.Authorization;
using Fleans.Application;
using Fleans.Application.QueryModels;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fleans.Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public partial class UserTasksController : ControllerBase
    {
        private readonly ILogger<UserTasksController> _logger;
        private readonly IWorkflowCommandService _commandService;
        private readonly IWorkflowQueryService _workflowQueryService;
        private readonly IUserGroupResolver _userGroupResolver;
        private readonly IUserIdResolver _userIdResolver;

        public UserTasksController(
            ILogger<UserTasksController> logger,
            IWorkflowCommandService commandService,
            IWorkflowQueryService workflowQueryService,
            IUserGroupResolver userGroupResolver,
            IUserIdResolver userIdResolver)
        {
            _logger = logger;
            _commandService = commandService;
            _workflowQueryService = workflowQueryService;
            _userGroupResolver = userGroupResolver;
            _userIdResolver = userIdResolver;
        }

        [EnableRateLimiting("read")]
        [HttpGet(Name = "GetPendingTasks")]
        public async Task<IActionResult> GetPendingTasks(
            [FromQuery] string? assignee = null,
            [FromQuery] string? candidateGroup = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? sorts = null,
            [FromQuery] string? filters = null)
        {
            var request = new PageRequest(page, pageSize, sorts, filters);
            var result = await _workflowQueryService.GetPendingUserTasks(assignee, candidateGroup, request);
            return Ok(result);
        }

        [EnableRateLimiting("read")]
        [HttpGet("{activityInstanceId:guid}", Name = "GetTask")]
        public async Task<IActionResult> GetTask(Guid activityInstanceId)
        {
            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            return Ok(task);
        }

        [EnableRateLimiting("task-operation")]
        [HttpPost("{activityInstanceId:guid}/claim", Name = "ClaimTask")]
        public async Task<IActionResult> ClaimTask(Guid activityInstanceId, [FromBody] ClaimTaskRequest? request)
        {
            request ??= new ClaimTaskRequest(null);
            if (!TryResolveUserId(activityInstanceId, request.UserId, out var userId, out var rejection))
                return rejection;

            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            try
            {
                var userGroups = _userGroupResolver.Resolve(HttpContext, request);
                LogUserTaskClaim(activityInstanceId, userId);
                await _commandService.ClaimUserTask(task.WorkflowInstanceId, activityInstanceId, userId, userGroups);
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new ErrorResponse(ex.Message));
            }
        }

        [EnableRateLimiting("task-operation")]
        [HttpPost("{activityInstanceId:guid}/unclaim", Name = "UnclaimTask")]
        public async Task<IActionResult> UnclaimTask(Guid activityInstanceId)
        {
            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            LogUserTaskUnclaim(activityInstanceId);
            await _commandService.UnclaimUserTask(task.WorkflowInstanceId, activityInstanceId);
            return Ok();
        }

        [EnableRateLimiting("task-operation")]
        [HttpPost("{activityInstanceId:guid}/complete", Name = "CompleteTask")]
        public async Task<IActionResult> CompleteTask(Guid activityInstanceId, [FromBody] CompleteTaskRequest? request)
        {
            if (!TryResolveUserId(activityInstanceId, request?.UserId, out var userId, out var rejection))
                return rejection;

            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            var variables = VariableConverter.ToExpandoObject(request?.Variables);

            try
            {
                LogUserTaskComplete(activityInstanceId, userId);
                await _commandService.CompleteUserTask(
                    task.WorkflowInstanceId, activityInstanceId, userId, variables);
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new ErrorResponse(ex.Message));
            }
        }

        [EnableRateLimiting("task-operation")]
        [HttpPost("{activityInstanceId:guid}/fail", Name = "FailTask")]
        public async Task<IActionResult> FailTask(Guid activityInstanceId, [FromBody] FailTaskRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ErrorMessage))
                return BadRequest(new ErrorResponse("ErrorMessage is required"));

            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
            {
                if (await _workflowQueryService.UserTaskExists(activityInstanceId))
                    return Ok();
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));
            }

            LogUserTaskFail(activityInstanceId, request.ErrorCode);
            await _commandService.FailUserTask(
                task.WorkflowInstanceId, activityInstanceId, request.ErrorCode, request.ErrorMessage);
            return Ok();
        }

        [EnableRateLimiting("task-operation")]
        [HttpPost("{activityInstanceId:guid}/cancel", Name = "CancelTask")]
        public async Task<IActionResult> CancelTask(Guid activityInstanceId, [FromBody] CancelTaskRequest? request)
        {
            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
            {
                if (await _workflowQueryService.UserTaskExists(activityInstanceId))
                    return Ok();
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));
            }

            LogUserTaskCancel(activityInstanceId);
            await _commandService.CancelUserTask(task.WorkflowInstanceId, activityInstanceId, request?.Reason);
            return Ok();
        }

        /// <summary>
        /// Resolves the acting user via <see cref="IUserIdResolver"/> (#793). Under JWT the
        /// token is authoritative and a differing body <c>UserId</c> is a 403. Rejection
        /// messages stay identifier-free.
        /// </summary>
        private bool TryResolveUserId(
            Guid activityInstanceId,
            string? bodyUserId,
            out string userId,
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
                    LogUserTaskUserIdMismatch(activityInstanceId);
                    rejection = StatusCode(StatusCodes.Status403Forbidden,
                        new ErrorResponse("UserId does not match the authenticated user"));
                    return false;
                case UserIdResolutionStatus.NoIdentityClaim:
                    LogUserTaskNoIdentityClaim(activityInstanceId);
                    rejection = StatusCode(StatusCodes.Status403Forbidden,
                        new ErrorResponse("Authenticated user has no user id claim"));
                    return false;
                default:
                    rejection = BadRequest(new ErrorResponse("UserId is required"));
                    return false;
            }
        }

        [LoggerMessage(EventId = 8004, Level = LogLevel.Information,
            Message = "Claiming user task {ActivityInstanceId} for user {UserId}")]
        private partial void LogUserTaskClaim(Guid activityInstanceId, string userId);

        [LoggerMessage(EventId = 8005, Level = LogLevel.Information,
            Message = "Unclaiming user task {ActivityInstanceId}")]
        private partial void LogUserTaskUnclaim(Guid activityInstanceId);

        [LoggerMessage(EventId = 8006, Level = LogLevel.Information,
            Message = "Completing user task {ActivityInstanceId} by user {UserId}")]
        private partial void LogUserTaskComplete(Guid activityInstanceId, string userId);

        [LoggerMessage(EventId = 8007, Level = LogLevel.Information,
            Message = "Failing user task {ActivityInstanceId} with error code {ErrorCode}")]
        private partial void LogUserTaskFail(Guid activityInstanceId, string errorCode);

        [LoggerMessage(EventId = 8008, Level = LogLevel.Information,
            Message = "Cancelling user task {ActivityInstanceId}")]
        private partial void LogUserTaskCancel(Guid activityInstanceId);

        [LoggerMessage(EventId = 8009, Level = LogLevel.Warning,
            Message = "Rejected user task operation on {ActivityInstanceId}: body UserId does not match the authenticated user")]
        private partial void LogUserTaskUserIdMismatch(Guid activityInstanceId);

        [LoggerMessage(EventId = 8010, Level = LogLevel.Warning,
            Message = "Rejected user task operation on {ActivityInstanceId}: authenticated principal has no user id claim (check Authentication:UserIdClaim)")]
        private partial void LogUserTaskNoIdentityClaim(Guid activityInstanceId);
    }
}
