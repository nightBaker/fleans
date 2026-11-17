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
- **Every action names a role policy** (#720). Roles: `admin` ⊇ `operator` ⊇ `user`, plus `deployer` and `debugger`, which grant one capability each and are included in `admin`. Most controllers carry `[Authorize(Policy = FleansPolicies.RequireOperator)]` (`RequireUser` for `UserTasksController`), and an action that disables, migrates, purges or changes settings adds `[Authorize(Policy = FleansPolicies.RequireAdmin)]`. Stacked attributes AND together, so `DefinitionsController` and `DeploymentsController` name a policy per action instead: their deploy actions use `RequireDeploy` (admin or deployer), which a controller-level `RequireOperator` would turn into operator-and-deployer. Then add the endpoint's row (with the roles allowed) to `Matrix` in `Fleans.Api.Tests/RoleAuthorizationTests.cs` and to the table in `website/src/content/docs/reference/authorization.md`. `EveryControllerAction_NamesARolePolicy` fails until both the attribute and the test row exist. With auth off the policies allow everyone, so dev mode is unaffected; `UseAuthorization` is therefore always in the pipeline, and a test host that maps controllers must call `AddFleansApiAuthorization(config, authEnabled: false)` + `UseAuthorization()` (see `AdmissionApiTests`).
- Admin UI (Fleans.Web) does NOT call HTTP API endpoints — it talks to Orleans grains directly via the `WorkflowEngine` service. Don't add API endpoints to back admin-UI features.

## Historical note — `/Workflow/*` prefix is gone

Pre-PR #587 a single `WorkflowController` exposed everything under `/Workflow/*`. The split moved each endpoint group to its own `/<ControllerName>/*` prefix. Existing consumers (self-host docs, manual test plans under `tests/manual/`) that hard-code `/workflow/*` must be updated to the new prefixes.
