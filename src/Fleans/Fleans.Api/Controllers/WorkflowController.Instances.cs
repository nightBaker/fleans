using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fleans.Api.Controllers
{
    public partial class WorkflowController
    {
        [EnableRateLimiting("polling")]
        [HttpGet("instances/{instanceId:guid}/state", Name = "GetInstanceState")]
        public async Task<IActionResult> GetInstanceState(Guid instanceId)
        {
            var snapshot = await _workflowQueryService.GetStateSnapshot(instanceId);
            if (snapshot is null)
                return NotFound(new ErrorResponse($"Instance {instanceId} not found"));
            return Ok(snapshot);
        }
    }
}
