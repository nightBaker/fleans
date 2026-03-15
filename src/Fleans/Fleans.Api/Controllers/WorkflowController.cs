using Fleans.Application;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Dynamic;

namespace Fleans.Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public partial class WorkflowController : ControllerBase
    {
        private readonly ILogger<WorkflowController> _logger;
        private readonly IWorkflowCommandService _commandService;
        private readonly IUserTaskQueryService _userTaskQueryService;

        public WorkflowController(
            ILogger<WorkflowController> logger,
            IWorkflowCommandService commandService,
            IUserTaskQueryService userTaskQueryService)
        {
            _logger = logger;
            _commandService = commandService;
            _userTaskQueryService = userTaskQueryService;
        }

        [HttpPost("start", Name = "StartWorkflow")]
        public async Task<IActionResult> StartWorkflow([FromBody] StartWorkflowRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.WorkflowId))
            {
                return BadRequest(new ErrorResponse("WorkflowId is required"));
            }

            try
            {
                var instanceId = await _commandService.StartWorkflow(request.WorkflowId);
                return Ok(new StartWorkflowResponse(instanceId));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse(ex.Message));
            }
        }

        [HttpPost("message", Name = "SendMessage")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.MessageName))
                return BadRequest(new ErrorResponse("MessageName is required"));

            try
            {
                // System.Text.Json deserializes ExpandoObject values as JsonElement,
                // which Orleans cannot serialize. Re-parse via Newtonsoft to get proper .NET primitives.
                var variables = request.Variables != null
                    ? JsonConvert.DeserializeObject<ExpandoObject>(
                        System.Text.Json.JsonSerializer.Serialize(request.Variables))!
                    : new ExpandoObject();

                var result = await _commandService.SendMessage(request.MessageName, request.CorrelationKey, variables);

                if (!result.Delivered)
                    return NotFound(new ErrorResponse(
                        $"No subscription or start event found for message '{request.MessageName}'"));

                return Ok(new SendMessageResponse(result.Delivered, result.WorkflowInstanceIds));
            }
            catch (Exception ex)
            {
                LogMessageDeliveryError(ex);
                return StatusCode(500, new ErrorResponse("An error occurred while delivering the message"));
            }
        }

        [LoggerMessage(EventId = 8002, Level = LogLevel.Error, Message = "Error delivering message")]
        private partial void LogMessageDeliveryError(Exception exception);

        [HttpPost("signal", Name = "SendSignal")]
        public async Task<IActionResult> SendSignal([FromBody] SendSignalRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SignalName))
                return BadRequest(new ErrorResponse("SignalName is required"));

            try
            {
                var result = await _commandService.SendSignal(request.SignalName);

                if (result.DeliveredCount == 0 && (result.WorkflowInstanceIds == null || result.WorkflowInstanceIds.Count == 0))
                    return NotFound(new ErrorResponse(
                        $"No subscription or start event found for signal '{request.SignalName}'"));

                return Ok(new SendSignalResponse(result.DeliveredCount, result.WorkflowInstanceIds));
            }
            catch (Exception ex)
            {
                LogSignalDeliveryError(ex);
                return StatusCode(500, new ErrorResponse("An error occurred while broadcasting the signal"));
            }
        }

        [LoggerMessage(EventId = 8003, Level = LogLevel.Error, Message = "Error broadcasting signal")]
        private partial void LogSignalDeliveryError(Exception exception);

        [HttpGet("tasks", Name = "GetPendingTasks")]
        public async Task<IActionResult> GetPendingTasks(
            [FromQuery] string? assignee = null,
            [FromQuery] string? candidateGroup = null)
        {
            var tasks = await _userTaskQueryService.GetPendingTasks(assignee, candidateGroup);
            return Ok(tasks);
        }

        [HttpGet("tasks/{activityInstanceId:guid}", Name = "GetTask")]
        public async Task<IActionResult> GetTask(Guid activityInstanceId)
        {
            var task = await _userTaskQueryService.GetTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            return Ok(task);
        }

        [HttpPost("tasks/{activityInstanceId:guid}/claim", Name = "ClaimTask")]
        public async Task<IActionResult> ClaimTask(Guid activityInstanceId, [FromBody] ClaimTaskRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest(new ErrorResponse("UserId is required"));

            var task = await _userTaskQueryService.GetTask(activityInstanceId);
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

        [HttpPost("tasks/{activityInstanceId:guid}/unclaim", Name = "UnclaimTask")]
        public async Task<IActionResult> UnclaimTask(Guid activityInstanceId)
        {
            var task = await _userTaskQueryService.GetTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            try
            {
                LogUserTaskUnclaim(activityInstanceId);
                await _commandService.UnclaimUserTask(task.WorkflowInstanceId, activityInstanceId);
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new ErrorResponse(ex.Message));
            }
        }

        [HttpPost("tasks/{activityInstanceId:guid}/complete", Name = "CompleteTask")]
        public async Task<IActionResult> CompleteTask(Guid activityInstanceId, [FromBody] CompleteTaskRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest(new ErrorResponse("UserId is required"));

            var task = await _userTaskQueryService.GetTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            try
            {
                var variables = new ExpandoObject();
                if (request.Variables is { Count: > 0 })
                {
                    var dict = (IDictionary<string, object?>)variables;
                    foreach (var kvp in request.Variables)
                        dict[kvp.Key] = kvp.Value;
                }

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

        [HttpPost("disable", Name = "DisableProcess")]
        public async Task<IActionResult> DisableProcess([FromBody] ProcessDefinitionKeyRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ProcessDefinitionKey))
                return BadRequest(new ErrorResponse("ProcessDefinitionKey is required"));

            try
            {
                var summary = await _commandService.DisableProcess(request.ProcessDefinitionKey);
                return Ok(summary);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse(ex.Message));
            }
        }

        [HttpPost("enable", Name = "EnableProcess")]
        public async Task<IActionResult> EnableProcess([FromBody] ProcessDefinitionKeyRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ProcessDefinitionKey))
                return BadRequest(new ErrorResponse("ProcessDefinitionKey is required"));

            try
            {
                var summary = await _commandService.EnableProcess(request.ProcessDefinitionKey);
                return Ok(summary);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse(ex.Message));
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
    }
}
