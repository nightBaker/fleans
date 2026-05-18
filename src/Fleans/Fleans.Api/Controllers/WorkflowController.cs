using Fleans.Application;
using Fleans.Infrastructure.Bpmn;
using Microsoft.AspNetCore.Mvc;

namespace Fleans.Api.Controllers
{
    // WorkflowController is split across partial files by concern:
    //   WorkflowController.cs              — header: ctor, fields, class-level attributes
    //   WorkflowController.Definitions.cs  — deploy, list-definitions, list-instances-by-key (×2), disable, enable
    //   WorkflowController.Execution.cs    — start, message, signal, evaluate-conditions, complete-activity
    //   WorkflowController.UserTasks.cs    — tasks list/get + claim/unclaim/complete/fail/cancel + LoggerMessage decls
    //   WorkflowController.Instances.cs    — instance state snapshot
    // All endpoints live under /workflow/* (preserved exactly). Each partial declares
    // `public partial class WorkflowController : ControllerBase` without class-level
    // attributes — the [ApiController] / [Route] below apply class-wide via merge.
    [ApiController]
    [Route("[controller]")]
    public partial class WorkflowController : ControllerBase
    {
        private readonly ILogger<WorkflowController> _logger;
        private readonly IWorkflowCommandService _commandService;
        private readonly IWorkflowQueryService _workflowQueryService;
        private readonly IBpmnConverter _bpmnConverter;

        public WorkflowController(
            ILogger<WorkflowController> logger,
            IWorkflowCommandService commandService,
            IWorkflowQueryService workflowQueryService,
            IBpmnConverter bpmnConverter)
        {
            _logger = logger;
            _commandService = commandService;
            _workflowQueryService = workflowQueryService;
            _bpmnConverter = bpmnConverter;
        }
    }
}
