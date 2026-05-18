using System.Text;
using Fleans.Application;
using Fleans.Application.QueryModels;
using Fleans.Infrastructure.Bpmn;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fleans.Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class DefinitionsController : ControllerBase
    {
        private readonly IWorkflowCommandService _commandService;
        private readonly IWorkflowQueryService _workflowQueryService;
        private readonly IBpmnConverter _bpmnConverter;

        public DefinitionsController(
            IWorkflowCommandService commandService,
            IWorkflowQueryService workflowQueryService,
            IBpmnConverter bpmnConverter)
        {
            _commandService = commandService;
            _workflowQueryService = workflowQueryService;
            _bpmnConverter = bpmnConverter;
        }

        [EnableRateLimiting("workflow-mutation")]
        [HttpPost("deploy", Name = "DeployWorkflow")]
        public async Task<IActionResult> DeployWorkflow([FromBody] DeployBpmnRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.BpmnXml))
                return BadRequest(new ErrorResponse("BpmnXml is required."));

            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request.BpmnXml));
                var workflow = await _bpmnConverter.ConvertFromXmlAsync(stream);
                var summary = await _commandService.DeployWorkflow(workflow, request.BpmnXml);
                return Ok(new DeployBpmnResponse(summary.ProcessDefinitionKey, summary.Version));
            }
            catch (Exception ex)
            {
                return BadRequest(new ErrorResponse($"Invalid BPMN: {ex.Message}"));
            }
        }

        [EnableRateLimiting("read")]
        [HttpGet(Name = "ListDefinitions")]
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
        [HttpGet("{key}/instances", Name = "ListInstancesByKey")]
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
        [HttpGet("{key}/{version:int}/instances", Name = "ListInstancesByKeyAndVersion")]
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
    }
}
