using Fleans.Application;
using Fleans.Application.Grains;
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
        private readonly IGrainFactory _grainFactory;

        public WorkflowController(
            ILogger<WorkflowController> logger,
            IWorkflowCommandService commandService,
            IGrainFactory grainFactory)
        {
            _logger = logger;
            _commandService = commandService;
            _grainFactory = grainFactory;
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

            if (string.IsNullOrWhiteSpace(request.CorrelationKey))
                return BadRequest(new ErrorResponse("CorrelationKey is required"));

            try
            {
                // System.Text.Json deserializes ExpandoObject values as JsonElement,
                // which Orleans cannot serialize. Re-parse via Newtonsoft to get proper .NET primitives.
                var variables = request.Variables != null
                    ? JsonConvert.DeserializeObject<ExpandoObject>(
                        System.Text.Json.JsonSerializer.Serialize(request.Variables))!
                    : new ExpandoObject();

                var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(request.MessageName);
                var delivered = await correlationGrain.DeliverMessage(
                    request.CorrelationKey,
                    variables);

                if (!delivered)
                    return NotFound(new ErrorResponse(
                        $"No subscription found for message '{request.MessageName}' with correlationKey '{request.CorrelationKey}'"));

                return Ok(new SendMessageResponse(Delivered: true));
            }
            catch (Exception ex)
            {
                LogMessageDeliveryError(ex);
                return StatusCode(500, new ErrorResponse("An error occurred while delivering the message"));
            }
        }

        [LoggerMessage(EventId = 8002, Level = LogLevel.Error, Message = "Error delivering message")]
        private partial void LogMessageDeliveryError(Exception exception);
    }
}
