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

        public UserTasksController(
            ILogger<UserTasksController> logger,
            IWorkflowCommandService commandService,
            IWorkflowQueryService workflowQueryService)
        {
            _logger = logger;
            _commandService = commandService;
            _workflowQueryService = workflowQueryService;
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
        public async Task<IActionResult> ClaimTask(Guid activityInstanceId, [FromBody] ClaimTaskRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest(new ErrorResponse("UserId is required"));

            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            try
            {
                LogUserTaskClaim(activityInstanceId, request.UserId);
                await _commandService.ClaimUserTask(task.WorkflowInstanceId, activityInstanceId, request.UserId);
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
        public async Task<IActionResult> CompleteTask(Guid activityInstanceId, [FromBody] CompleteTaskRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest(new ErrorResponse("UserId is required"));

            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            var variables = VariableConverter.ToExpandoObject(request.Variables);

            try
            {
                LogUserTaskComplete(activityInstanceId, request.UserId);
                await _commandService.CompleteUserTask(
                    task.WorkflowInstanceId, activityInstanceId, request.UserId, variables);
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
    }
}
