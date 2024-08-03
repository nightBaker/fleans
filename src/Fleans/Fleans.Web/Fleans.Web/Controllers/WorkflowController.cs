using Fleans.Application;
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

        [HttpPost(Name = "Start")]
        public async Task<IActionResult> StartWorkflow(string workflowId)
        {
            await _workflowEngine.StartWorkflow(workflowId);

            return Ok();
        }
    }
}