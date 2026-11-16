using Fleans.Application;
using Fleans.Application.InstanceMigration;
using Fleans.Application.QueryModels;
using Fleans.Domain.InstanceMigration;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fleans.Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class InstancesController : ControllerBase
    {
        private readonly IWorkflowQueryService _workflowQueryService;
        private readonly IWorkflowInstancePurgeService _purgeService;
        private readonly IInstanceMigrationService _migrationService;

        public InstancesController(
            IWorkflowQueryService workflowQueryService,
            IWorkflowInstancePurgeService purgeService,
            IInstanceMigrationService migrationService)
        {
            _workflowQueryService = workflowQueryService;
            _purgeService = purgeService;
            _migrationService = migrationService;
        }

        [EnableRateLimiting("polling")]
        [HttpGet("{instanceId:guid}/state", Name = "GetInstanceState")]
        public async Task<IActionResult> GetInstanceState(Guid instanceId)
        {
            var snapshot = await _workflowQueryService.GetStateSnapshot(instanceId);
            if (snapshot is null)
                return NotFound(new ErrorResponse($"Instance {instanceId} not found"));
            return Ok(snapshot);
        }

        /// <summary>
        /// Retention (#862): deletes the journal, snapshot and read-model rows of instances that
        /// finished (completed, cancelled or failed) before <c>CompletedBefore</c>. Bounded per call;
        /// also removes message-deduplication records whose 24-hour window has expired.
        /// repeat while <c>HasMore</c>. Runbook: <c>docs/runbooks/instance-retention.md</c>.
        /// </summary>
        [EnableRateLimiting("admin")]
        [HttpPost("purge", Name = "PurgeInstances")]
        public async Task<IActionResult> PurgeInstances([FromBody] PurgeInstancesRequest request, CancellationToken cancellationToken)
        {
            if (request?.CompletedBefore is not { } completedBefore)
                return BadRequest(new ErrorResponse("CompletedBefore is required"));

            // Out-of-range values throw ArgumentOutOfRangeException → 400 (GlobalExceptionHandler).
            var result = await _purgeService.PurgeCompletedAsync(
                completedBefore,
                request.BatchSize ?? IWorkflowInstancePurgeService.DefaultBatchSize,
                request.MaxInstances ?? IWorkflowInstancePurgeService.DefaultMaxInstances,
                cancellationToken);
            return Ok(new PurgeInstancesResponse(result.PurgedCount, result.HasMore, result.PurgedMessageDeduplicationsCount));
        }

        /// <summary>
        /// Moves one running instance to another version of its process (#1069). A rejection is
        /// 409 with its codes; a dry run answers 200 with the same report and changes nothing.
        /// </summary>
        [EnableRateLimiting("admin")]
        [HttpPost("{instanceId:guid}/migrate", Name = "MigrateInstance")]
        public async Task<IActionResult> MigrateInstance(Guid instanceId, [FromBody] MigrateInstanceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.TargetProcessDefinitionId))
                return BadRequest(new ErrorResponse("TargetProcessDefinitionId is required"));

            // Unknown instance or target version → KeyNotFoundException → 404 (GlobalExceptionHandler).
            var result = await _migrationService.MigrateInstanceAsync(instanceId, new InstanceMigrationRequest(
                [instanceId], null, request.TargetProcessDefinitionId, ToMappings(request.Mappings),
                request.UpdateEventTriggers, request.DryRun, request.RequestId));
            var dto = ToDto(result);
            return result.Outcome == InstanceMigrationOutcome.Rejected && !request.DryRun ? Conflict(dto) : Ok(dto);
        }

        /// <summary>
        /// Moves a batch of running instances (#1069): an explicit list, or the running instances of
        /// a version a page at a time. Always 200 with a per-instance report.
        /// </summary>
        [EnableRateLimiting("admin")]
        [HttpPost("migrate", Name = "MigrateInstances")]
        public async Task<IActionResult> MigrateInstances([FromBody] MigrateInstancesRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request?.TargetProcessDefinitionId))
                return BadRequest(new ErrorResponse("TargetProcessDefinitionId is required"));

            // Neither or both selectors, an over-long list or a bad token → ArgumentException → 400.
            var batch = await _migrationService.MigrateAsync(new InstanceMigrationRequest(
                request.InstanceIds, request.SourceProcessDefinitionId, request.TargetProcessDefinitionId,
                ToMappings(request.Mappings), request.UpdateEventTriggers, request.DryRun,
                request.RequestId, request.ContinuationToken), cancellationToken);

            var results = batch.Results.Select(ToDto).ToList();
            return Ok(new MigrateInstancesResponse(
                batch.RequestId,
                batch.DryRun,
                results.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count()),
                results.SelectMany(r => r.Rejections.Select(x => x.Code).Distinct())
                    .GroupBy(code => code).ToDictionary(g => g.Key, g => g.Count()),
                results,
                batch.ContinuationToken));
        }

        private static List<ActivityMapping> ToMappings(List<MigrationMappingDto>? mappings)
            => mappings?.Select(m => new ActivityMapping(m.SourceActivityId, m.TargetActivityId)).ToList() ?? [];

        private static InstanceMigrationResultDto ToDto(InstanceMigrationResult result) => new(
            result.InstanceId,
            result.Outcome.ToString(),
            result.SourceProcessDefinitionId,
            result.TargetProcessDefinitionId,
            result.Rejections.Select(r => new MigrationRejectionDto(r.Code, r.ActivityId, r.Message)).ToList(),
            result.Error);
    }
}
