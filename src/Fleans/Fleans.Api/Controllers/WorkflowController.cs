using Fleans.Application;
using Fleans.Application.QueryModels;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
        private readonly IWorkflowQueryService _workflowQueryService;

        public WorkflowController(
            ILogger<WorkflowController> logger,
            IWorkflowCommandService commandService,
            IWorkflowQueryService workflowQueryService)
        {
            _logger = logger;
            _commandService = commandService;
            _workflowQueryService = workflowQueryService;
        }

        [EnableRateLimiting("workflow-mutation")]
        [HttpPost("start", Name = "StartWorkflow")]
        public async Task<IActionResult> StartWorkflow([FromBody] StartWorkflowRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.WorkflowId))
            {
                return BadRequest(new ErrorResponse("WorkflowId is required"));
            }

            var instanceId = await _commandService.StartWorkflow(request.WorkflowId);
            return Ok(new StartWorkflowResponse(instanceId));
        }

        [EnableRateLimiting("workflow-mutation")]
        [HttpPost("message", Name = "SendMessage")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.MessageName))
                return BadRequest(new ErrorResponse("MessageName is required"));

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

        [EnableRateLimiting("workflow-mutation")]
        [HttpPost("signal", Name = "SendSignal")]
        public async Task<IActionResult> SendSignal([FromBody] SendSignalRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SignalName))
                return BadRequest(new ErrorResponse("SignalName is required"));

            var result = await _commandService.SendSignal(request.SignalName);

            if (result.DeliveredCount == 0 && (result.WorkflowInstanceIds == null || result.WorkflowInstanceIds.Count == 0))
                return NotFound(new ErrorResponse(
                    $"No subscription or start event found for signal '{request.SignalName}'"));

            return Ok(new SendSignalResponse(result.DeliveredCount, result.WorkflowInstanceIds));
        }

        [EnableRateLimiting("task-operation")]
        [HttpPost("complete-activity", Name = "CompleteActivity")]
        public async Task<IActionResult> CompleteActivity([FromBody] CompleteActivityRequest request)
        {
            if (request == null || request.WorkflowInstanceId == Guid.Empty)
                return BadRequest(new ErrorResponse("WorkflowInstanceId is required"));
            if (string.IsNullOrWhiteSpace(request.ActivityId))
                return BadRequest(new ErrorResponse("ActivityId is required"));

            // System.Text.Json deserializes ExpandoObject values as JsonElement,
            // which Orleans cannot serialize. Re-parse via Newtonsoft to get proper .NET primitives.
            var variables = request.Variables != null
                ? JsonConvert.DeserializeObject<ExpandoObject>(
                    System.Text.Json.JsonSerializer.Serialize(request.Variables))!
                : new ExpandoObject();

            await _commandService.CompleteActivity(request.WorkflowInstanceId, request.ActivityId, variables);
            return Ok();
        }

        [EnableRateLimiting("read")]
        [HttpGet("definitions", Name = "ListDefinitions")]
        public async Task<IActionResult> ListDefinitions(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? sorts = null,
            [FromQuery] string? filters = null)
        {
            var request = new PageRequest(page, pageSize, sorts, filters);
            var result = await _workflowQueryService.GetAllProcessDefinitions(request);
            return Ok(result);
        }

        [EnableRateLimiting("read")]
        [HttpGet("definitions/{key}/instances", Name = "ListInstancesByKey")]
        public async Task<IActionResult> ListInstancesByKey(
            string key,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? sorts = null,
            [FromQuery] string? filters = null)
        {
            var request = new PageRequest(page, pageSize, sorts, filters);
            var result = await _workflowQueryService.GetInstancesByKey(key, request);
            return Ok(result);
        }

        [EnableRateLimiting("read")]
        [HttpGet("definitions/{key}/{version:int}/instances", Name = "ListInstancesByKeyAndVersion")]
        public async Task<IActionResult> ListInstancesByKeyAndVersion(
            string key, int version,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? sorts = null,
            [FromQuery] string? filters = null)
        {
            var request = new PageRequest(page, pageSize, sorts, filters);
            var result = await _workflowQueryService.GetInstancesByKeyAndVersion(key, version, request);
            return Ok(result);
        }

        [EnableRateLimiting("read")]
        [HttpGet("tasks", Name = "GetPendingTasks")]
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
        [HttpGet("tasks/{activityInstanceId:guid}", Name = "GetTask")]
        public async Task<IActionResult> GetTask(Guid activityInstanceId)
        {
            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            return Ok(task);
        }

        [EnableRateLimiting("task-operation")]
        [HttpPost("tasks/{activityInstanceId:guid}/claim", Name = "ClaimTask")]
        public async Task<IActionResult> ClaimTask(Guid activityInstanceId, [FromBody] ClaimTaskRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest(new ErrorResponse("UserId is required"));

            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            LogUserTaskClaim(activityInstanceId, request.UserId);
            await _commandService.ClaimUserTask(task.WorkflowInstanceId, activityInstanceId, request.UserId);
            return Ok();
        }

        [EnableRateLimiting("task-operation")]
        [HttpPost("tasks/{activityInstanceId:guid}/unclaim", Name = "UnclaimTask")]
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
        [HttpPost("tasks/{activityInstanceId:guid}/complete", Name = "CompleteTask")]
        public async Task<IActionResult> CompleteTask(Guid activityInstanceId, [FromBody] CompleteTaskRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.UserId))
                return BadRequest(new ErrorResponse("UserId is required"));

            var task = await _workflowQueryService.GetUserTask(activityInstanceId);
            if (task == null)
                return NotFound(new ErrorResponse($"User task '{activityInstanceId}' not found"));

            // System.Text.Json deserializes values as JsonElement,
            // which Orleans cannot serialize. Re-parse via Newtonsoft to get proper .NET primitives.
            var variables = request.Variables is { Count: > 0 }
                ? JsonConvert.DeserializeObject<ExpandoObject>(
                    System.Text.Json.JsonSerializer.Serialize(request.Variables))!
                : new ExpandoObject();

            LogUserTaskComplete(activityInstanceId, request.UserId);
            await _commandService.CompleteUserTask(
                task.WorkflowInstanceId, activityInstanceId, request.UserId, variables);
            return Ok();
        }

        [EnableRateLimiting("admin")]
        [HttpPost("disable", Name = "DisableProcess")]
        public async Task<IActionResult> DisableProcess([FromBody] ProcessDefinitionKeyRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ProcessDefinitionKey))
                return BadRequest(new ErrorResponse("ProcessDefinitionKey is required"));

            var summary = await _commandService.DisableProcess(request.ProcessDefinitionKey);
            return Ok(summary);
        }

        [EnableRateLimiting("admin")]
        [HttpPost("enable", Name = "EnableProcess")]
        public async Task<IActionResult> EnableProcess([FromBody] ProcessDefinitionKeyRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ProcessDefinitionKey))
                return BadRequest(new ErrorResponse("ProcessDefinitionKey is required"));

            var summary = await _commandService.EnableProcess(request.ProcessDefinitionKey);
            return Ok(summary);
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
