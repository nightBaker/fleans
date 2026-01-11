using Fleans.Application;
using Fleans.Domain;
using Fleans.Infrastructure.Bpmn;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Fleans.Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WorkflowController : ControllerBase
    {
        
        private readonly ILogger<WorkflowController> _logger;
        private readonly WorkflowEngine _workflowEngine;
        private readonly IBpmnConverter _bpmnConverter;

        public WorkflowController(
            ILogger<WorkflowController> logger, 
            WorkflowEngine workflowEngine,
            IBpmnConverter bpmnConverter)
        {
            _logger = logger;
            _workflowEngine = workflowEngine;
            _bpmnConverter = bpmnConverter;
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
                var instanceId = await _workflowEngine.StartWorkflow(request.WorkflowId);
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
//TODO : explicitly validate file content type and size if needed
            if (!file.FileName.EndsWith(".bpmn", StringComparison.OrdinalIgnoreCase) &&
                !file.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new ErrorResponse("File must be a BPMN (.bpmn) or XML (.xml) file"));
            }

            try
            {
                using var stream = file.OpenReadStream();
                var workflow = await _bpmnConverter.ConvertFromXmlAsync(stream);
                
                await _workflowEngine.RegisterWorkflow(workflow);

                return Ok(new UploadBpmnResponse(
                    Message: "BPMN file uploaded and workflow registered successfully",
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
                _logger.LogError(ex, "Error processing BPMN file");
                return StatusCode(500, new ErrorResponse("An error occurred while processing the BPMN file"));
            }
        }

        [HttpPost("register", Name = "RegisterWorkflow")]
        public async Task<IActionResult> RegisterWorkflow([FromBody] WorkflowDefinition workflow)
        {
            try
            {
                await _workflowEngine.RegisterWorkflow(workflow);
                return Ok(new RegisterWorkflowResponse("Workflow registered successfully", workflow.WorkflowId));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ErrorResponse(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new ErrorResponse(ex.Message));
            }
        }
    }
}