using Fleans.Application;
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

        public InstancesController(IWorkflowQueryService workflowQueryService, IWorkflowInstancePurgeService purgeService)
        {
            _workflowQueryService = workflowQueryService;
            _purgeService = purgeService;
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
    }
}
