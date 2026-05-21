# Adding a REST API endpoint

The workflow REST surface is split across four separate controllers by concern, each with its own DI dependencies and `[Route("[controller]")]`:

| Controller | Route prefix | Endpoints |
|---|---|---|
| `WorkflowDefinitionsController` | `/WorkflowDefinitions` | deploy, list definitions, list instances by key, disable/enable process |
| `WorkflowExecutionController` | `/WorkflowExecution` | start, message, signal, evaluate-conditions, complete-activity |
| `UserTasksController` | `/UserTasks` | list, get, claim, unclaim, complete, fail, cancel (+ user-task `[LoggerMessage]` declarations) |
| `WorkflowInstancesController` | `/WorkflowInstances` | instance state snapshot |

## Conventions

- Add new endpoints to whichever controller's concern matches. Don't create a new controller unless the concern is genuinely new.
- Each controller takes only the services it needs (e.g. `WorkflowInstancesController` only depends on `IWorkflowQueryService`, not the full Command/Query/BpmnConverter triplet).
- DTOs go in `Fleans.ServiceDefaults/DTOs/`.
- Admin UI (Fleans.Web) does NOT call HTTP API endpoints — it talks to Orleans grains directly via the `WorkflowEngine` service. Don't add API endpoints to back admin-UI features.

## Historical note — `/Workflow/*` prefix is gone

Pre-PR #587 a single `WorkflowController` exposed everything under `/Workflow/*`. The split moved each endpoint group to its own `/<ControllerName>/*` prefix. Existing consumers (self-host docs, manual test plans under `tests/manual/`) that hard-code `/workflow/*` must be updated to the new prefixes.
