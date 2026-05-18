using Fleans.Application;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fleans.Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WorkflowExecutionController : ControllerBase
    {
        private readonly IWorkflowCommandService _commandService;

        public WorkflowExecutionController(IWorkflowCommandService commandService)
        {
            _commandService = commandService;
        }

        [EnableRateLimiting("workflow-mutation")]
        [HttpPost("start", Name = "StartWorkflow")]
        public async Task<IActionResult> StartWorkflow([FromBody] StartWorkflowRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.WorkflowId))
            {
                return BadRequest(new ErrorResponse("WorkflowId is required"));
            }

            var initialVariables = request.Variables != null
                ? VariableConverter.ToExpandoObject(request.Variables)
                : null;

            var instanceId = await _commandService.StartWorkflow(request.WorkflowId, initialVariables);
            return Ok(new StartWorkflowResponse(instanceId));
        }

        [EnableRateLimiting("workflow-mutation")]
        [HttpPost("message", Name = "SendMessage")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.MessageName))
                return BadRequest(new ErrorResponse("MessageName is required"));

            var variables = VariableConverter.ToExpandoObject(request.Variables);

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

        [EnableRateLimiting("workflow-mutation")]
        [HttpPost("evaluate-conditions", Name = "EvaluateConditions")]
        public async Task<IActionResult> EvaluateConditions([FromBody] EvaluateConditionsRequest request)
        {
            if (request == null)
                return BadRequest(new ErrorResponse("Request body is required"));

            if (request.Variables == null || request.Variables.Count == 0)
                return BadRequest(new ErrorResponse("Variables are required for condition evaluation"));

            var variables = VariableConverter.ToExpandoObject(request.Variables);
            var result = await _commandService.EvaluateConditions(request.WorkflowId, variables);
            return Ok(new EvaluateConditionsResponse(result.StartedInstanceIds, result.Errors));
        }

        [EnableRateLimiting("task-operation")]
        [HttpPost("complete-activity", Name = "CompleteActivity")]
        public async Task<IActionResult> CompleteActivity([FromBody] CompleteActivityRequest request)
        {
            if (request == null || request.WorkflowInstanceId == Guid.Empty)
                return BadRequest(new ErrorResponse("WorkflowInstanceId is required"));
            if (string.IsNullOrWhiteSpace(request.ActivityId))
                return BadRequest(new ErrorResponse("ActivityId is required"));

            var variables = VariableConverter.ToExpandoObject(request.Variables);

            await _commandService.CompleteActivity(request.WorkflowInstanceId, request.ActivityId, variables);
            return Ok();
        }
    }
}
