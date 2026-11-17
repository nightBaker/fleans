using Fleans.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using System.Text;
using Fleans.Application;
using Fleans.Application.QueryModels;
using Fleans.Application.Bpmn;
using Fleans.Application.Deployments;
using Fleans.Application.InstanceMigration;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fleans.Api.Controllers
{
    [Authorize(Policy = FleansPolicies.RequireOperator)]
    [ApiController]
    [Route("[controller]")]
    public class DefinitionsController : ControllerBase
    {
        private readonly IWorkflowCommandService _commandService;
        private readonly IWorkflowQueryService _workflowQueryService;
        private readonly IBpmnConverter _bpmnConverter;
        private readonly IInstanceMigrationService _migrationService;

        public DefinitionsController(
            IWorkflowCommandService commandService,
            IWorkflowQueryService workflowQueryService,
            IBpmnConverter bpmnConverter,
            IInstanceMigrationService migrationService)
        {
            _commandService = commandService;
            _workflowQueryService = workflowQueryService;
            _bpmnConverter = bpmnConverter;
            _migrationService = migrationService;
        }

        /// <summary>
        /// The default (same-id) mapping from version <paramref name="from"/> to version
        /// <paramref name="to"/> (default: the latest) and how many instances of
        /// <paramref name="from"/> are running (#1069). Feeds the migration dialog; a dry run
        /// (<c>POST /Instances/migrate</c>) answers per instance.
        /// </summary>
        [EnableRateLimiting("read")]
        [HttpGet("{key}/versions/{from:int}/migration-preview", Name = "GetMigrationPreview")]
        public async Task<IActionResult> GetMigrationPreview(string key, int from, [FromQuery] int? to = null)
        {
            var target = to ?? await _workflowQueryService.GetMaxVersionByKeyAsync(key);
            var preview = await _migrationService.PreviewAsync(key, from, target);
            if (preview is null)
                return NotFound(new ErrorResponse($"Process '{key}' has no version {from} or {target}"));

            return Ok(new MigrationPreviewResponse(
                preview.ProcessDefinitionKey, preview.SourceProcessDefinitionId, preview.SourceVersion,
                preview.TargetProcessDefinitionId, preview.TargetVersion, preview.RunningInstanceCount,
                preview.Elements.Select(e => new MigrationPreviewElementDto(
                    e.SourceActivityId, e.SourceActivityType, e.TargetActivityId, e.TargetActivityType, e.Problem)).ToList(),
                preview.TargetElements.Select(e => new MigrationTargetElementDto(e.ActivityId, e.ActivityType)).ToList()));
        }

        [EnableRateLimiting("workflow-mutation")]
        [Authorize(Policy = FleansPolicies.RequireAdmin)]
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
                return Ok(new DeployBpmnResponse(summary.ProcessDefinitionKey, summary.Version, summary.Warnings));
            }
            catch (DeploymentApprovalRequiredException ex)
            {
                return Conflict(new ErrorResponse(ex.Message));
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

        /// <summary>
        /// The latest version of each process in <paramref name="keys"/> with a SHA-256 of its BPMN,
        /// so another environment's Admin UI can tell whether it runs the same diagram (#900).
        /// </summary>
        [EnableRateLimiting("read")]
        [HttpGet("latest", Name = "ListLatestVersions")]
        public async Task<IActionResult> ListLatestVersions([FromQuery] string[] keys)
        {
            if (keys.Length > MaxLatestKeys)
                return BadRequest(new ErrorResponse($"At most {MaxLatestKeys} keys per request."));
            var latest = await _workflowQueryService.GetVersionFingerprints(keys.Distinct().ToList(), latestOnly: true);
            return Ok(latest);
        }

        private const int MaxLatestKeys = 100;

        [EnableRateLimiting("read")]
        [HttpGet("{key}/instances", Name = "ListInstancesByKey")]
        public async Task<IActionResult> ListInstancesByKey(
            string key,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? sorts = null,
            [FromQuery] string? filters = null,
            [FromQuery] string? businessId = null)
        {
            var request = new PageRequest(page, pageSize, sorts, filters);
            var result = await _workflowQueryService.GetInstancesByKey(key, request, businessId);
            return Ok(result);
        }

        [EnableRateLimiting("read")]
        [HttpGet("{key}/{version:int}/instances", Name = "ListInstancesByKeyAndVersion")]
        public async Task<IActionResult> ListInstancesByKeyAndVersion(
            string key, int version,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? sorts = null,
            [FromQuery] string? filters = null,
            [FromQuery] string? businessId = null)
        {
            var request = new PageRequest(page, pageSize, sorts, filters);
            var result = await _workflowQueryService.GetInstancesByKeyAndVersion(key, version, request, businessId);
            return Ok(result);
        }

        [EnableRateLimiting("admin")]
        [Authorize(Policy = FleansPolicies.RequireAdmin)]
        [HttpPost("disable", Name = "DisableProcess")]
        public async Task<IActionResult> DisableProcess([FromBody] ProcessDefinitionKeyRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ProcessDefinitionKey))
                return BadRequest(new ErrorResponse("ProcessDefinitionKey is required"));

            var summary = await _commandService.DisableProcess(request.ProcessDefinitionKey);
            return Ok(summary);
        }

        [EnableRateLimiting("admin")]
        [Authorize(Policy = FleansPolicies.RequireAdmin)]
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
