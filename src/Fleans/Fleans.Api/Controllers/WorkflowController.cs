using Fleans.Application;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Infrastructure.Bpmn;
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
        private const long MaxBpmnFileSizeBytes = 10 * 1024 * 1024; // 10 MB

        private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "text/xml",
            "application/xml",
            "application/octet-stream"
        };

        private readonly ILogger<WorkflowController> _logger;
        private readonly IWorkflowCommandService _commandService;
        private readonly IWorkflowQueryService _queryService;
        private readonly IBpmnConverter _bpmnConverter;
        private readonly IGrainFactory _grainFactory;

        public WorkflowController(
            ILogger<WorkflowController> logger,
            IWorkflowCommandService commandService,
            IWorkflowQueryService queryService,
            IBpmnConverter bpmnConverter,
            IGrainFactory grainFactory)
        {
            _logger = logger;
            _commandService = commandService;
            _queryService = queryService;
            _bpmnConverter = bpmnConverter;
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

        [HttpPost("upload-bpmn", Name = "UploadBpmn")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadBpmn(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new ErrorResponse("No file uploaded"));
            }

            if (file.Length > MaxBpmnFileSizeBytes)
            {
                return BadRequest(new ErrorResponse("File size exceeds the 10 MB limit"));
            }

            if (!AllowedContentTypes.Contains(file.ContentType))
            {
                return BadRequest(new ErrorResponse(
                    $"Unsupported content type '{file.ContentType}'. Allowed types: {string.Join(", ", AllowedContentTypes)}"));
            }

            if (!file.FileName.EndsWith(".bpmn", StringComparison.OrdinalIgnoreCase) &&
                !file.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new ErrorResponse("File must be a BPMN (.bpmn) or XML (.xml) file"));
            }

            try
            {
                using var reader = new StreamReader(file.OpenReadStream());
                var bpmnXml = await reader.ReadToEndAsync();

                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmnXml));
                var workflow = await _bpmnConverter.ConvertFromXmlAsync(stream);

                await _commandService.DeployWorkflow(workflow, bpmnXml);

                return Ok(new UploadBpmnResponse(
                    Message: "BPMN file uploaded and workflow deployed successfully",
                    WorkflowId: workflow.WorkflowId,
                    ActivitiesCount: workflow.Activities.Count,
                    SequenceFlowsCount: workflow.SequenceFlows.Count));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ErrorResponse($"Invalid BPMN file: {ex.Message}"));
            }
            catch (Exception ex)
            {
                LogBpmnUploadError(ex);
                return StatusCode(500, new ErrorResponse("An error occurred while processing the BPMN file"));
            }
        }

        [HttpGet("all", Name = "GetAllWorkflows")]
        public async Task<IActionResult> GetAllWorkflows()
        {
            try
            {
                var definitions = await _queryService.GetAllProcessDefinitions();
                return Ok(definitions);
            }
            catch (Exception ex)
            {
                LogGetAllWorkflowsError(ex);
                return StatusCode(500, new ErrorResponse("An error occurred while retrieving workflows"));
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

        [LoggerMessage(EventId = 8000, Level = LogLevel.Error, Message = "Error processing BPMN file")]
        private partial void LogBpmnUploadError(Exception exception);

        [LoggerMessage(EventId = 8001, Level = LogLevel.Error, Message = "Error retrieving workflows")]
        private partial void LogGetAllWorkflowsError(Exception exception);

        [LoggerMessage(EventId = 8002, Level = LogLevel.Error, Message = "Error delivering message")]
        private partial void LogMessageDeliveryError(Exception exception);
    }
}
