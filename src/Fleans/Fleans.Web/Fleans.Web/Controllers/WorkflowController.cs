using Fleans.Application;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Fleans.Web.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WorkflowController : ControllerBase
    {

        private readonly ILogger<WorkflowController> _logger;
        private readonly WorkflowEngine _workflowEngine;

        public WorkflowController(ILogger<WorkflowController> logger, WorkflowEngine workflowEngine)
        {
            _logger = logger;
            _workflowEngine = workflowEngine;
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

        [HttpGet("all", Name = "GetAllWorkflows")]
        public async Task<IActionResult> GetAllWorkflows()
        {
            try
            {
                var workflows = await _workflowEngine.GetAllWorkflows();
                return Ok(workflows);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving workflows");
                return StatusCode(500, new ErrorResponse("An error occurred while retrieving workflows"));
            }
        }
    }
}