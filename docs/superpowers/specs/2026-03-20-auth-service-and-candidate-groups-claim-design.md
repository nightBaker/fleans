# Auth Service & CandidateGroups Claim Authorization

**Date:** 2026-03-20

## Problem

`ClaimUserTask` and `CompleteUserTask` have two gaps:

1. `userId` is passed in the request body (`ClaimTaskRequest`, `CompleteTaskRequest`) instead of being resolved from the authenticated HTTP context.
2. `CandidateGroups` is stored on `UserTaskMetadata` but never checked during claim authorization — any user can claim any task regardless of group membership.

## Goals

- Introduce `IAuthService` to encapsulate current-user identity and group resolution.
- Thread `userId` + `userGroups` through the **claim** call chain; thread only `userId` through the **complete** call chain (completion auth checks `ClaimedBy == userId`, which does not involve groups).
- Enforce Camunda-style authorization in `WorkflowExecution.ClaimUserTask`.
- Keep the implementation abstract and provide a fake for now.

## Design

### `IAuthService` (new — `Fleans.Api`)

```csharp
public interface IAuthService
{
    string GetCurrentUserId();
    Task<IReadOnlyList<string>> GetCurrentUserGroupsAsync();
}
```

Placed in `Fleans.Api` alongside its implementations — it is a controller-only concern. The application layer (grains, command service) must never inject or call `IAuthService` directly. Identity is passed down as plain `string`/`IReadOnlyList<string>` method parameters. No `[GenerateSerializer]` is needed because these values never cross a grain boundary.

### `FakeAuthService` (new — `Fleans.Api`)

```csharp
public class FakeAuthService : IAuthService
{
    private readonly IConfiguration _config;

    public FakeAuthService(IConfiguration config) => _config = config;

    public string GetCurrentUserId()
        => _config["FakeAuth:UserId"] ?? "dev-user";

    public Task<IReadOnlyList<string>> GetCurrentUserGroupsAsync()
    {
        var groups = _config.GetSection("FakeAuth:Groups").Get<string[]>() ?? [];
        return Task.FromResult<IReadOnlyList<string>>(groups);
    }
}
```

Reads from `appsettings.Development.json` so groups can be changed without recompile (e.g., `"FakeAuth": { "UserId": "dev-user", "Groups": ["managers"] }`). Registered as **singleton** in `Fleans.Api` DI — it holds no per-request state.

### `HttpContextAuthService` (new — `Fleans.Api`, not registered yet)

```csharp
public class HttpContextAuthService : IAuthService
{
    // Reads ClaimTypes.NameIdentifier from IHttpContextAccessor.HttpContext.User
    // GetCurrentUserGroupsAsync() throws NotImplementedException until a real group provider is wired
}
```

Not registered — placeholder for when real authentication is added. `GetCurrentUserGroupsAsync` must throw `NotImplementedException` so that misconfigured environments fail loudly with a 500, rather than silently allowing all group checks to pass. This is intentional — do not add a catch block for `NotImplementedException` in the controller. Registration also requires `services.AddHttpContextAccessor()`.

### Controller changes (`WorkflowController`)

Inject `IAuthService`.

**`ClaimTask`:**
- Remove `[FromBody] ClaimTaskRequest request` parameter and the `UserId` null-check.
- `var userId = authService.GetCurrentUserId();` — if null or empty, `return Unauthorized()`.
- `var userGroups = await authService.GetCurrentUserGroupsAsync();`
- Update `LogUserTaskClaim(activityInstanceId, userId)` call site — `userId` now comes from auth service, not request body.
- Pass both to `IWorkflowCommandService.ClaimUserTask`.
- Add `catch (BadRequestActivityException ex) { return BadRequest(new ErrorResponse(ex.Message)); }` — auth failures from the domain must map to 400, not 500.

**`CompleteTask`:**
- Remove `UserId` from `CompleteTaskRequest` (retain `Variables`). **Breaking API change** — callers currently send `{"UserId":"...", "Variables":{}}` and must update to `{"Variables":{}}`.
- `var userId = authService.GetCurrentUserId();` — same null guard, return 401 if empty.
- Update `LogUserTaskComplete(activityInstanceId, userId)` call site.
- Apply the same Newtonsoft re-parse pattern already used in `CompleteActivity` to convert `Variables` from `JsonElement` to an `ExpandoObject` — do not change this existing logic.
- Pass `userId` to `IWorkflowCommandService.CompleteUserTask` (no `userGroups` — completion checks `ClaimedBy == userId`).
- Add `catch (BadRequestActivityException ex) { return BadRequest(new ErrorResponse(ex.Message)); }`.

`ClaimTaskRequest` record is **deleted entirely** — the claim endpoint takes no request body. **Breaking API change.**
`CompleteTaskRequest` becomes `record CompleteTaskRequest(Dictionary<string, object?>? Variables)`.

### Call chain

The table below describes **signature changes only**. Controller-level error handling changes (adding `catch BadRequestActivityException`) are not shown here but are described above.

| Layer | ClaimUserTask | CompleteUserTask |
|---|---|---|
| `IWorkflowCommandService` | `(instanceId, activityId, userId, userGroups)` | unchanged |
| `WorkflowCommandService` | `LogClaimingUserTask` updated to include `groupCount` (see Logging); pass through | pass through |
| `IWorkflowInstanceGrain` | `(activityId, userId, userGroups)` | unchanged |
| `WorkflowInstance.cs` (public entry points) | accept and forward `userGroups` | unchanged |
| `WorkflowExecution` | `(activityId, userId, userGroups)` — new auth logic | unchanged |

### Authorization logic in `WorkflowExecution.ClaimUserTask`

Preserve the existing two-branch structure, extending "candidate users" to include group membership:

```csharp
var isAssignee = metadata.Assignee == userId;
var isCandidateUser = metadata.CandidateUsers.Contains(userId);
var isCandidateGroup = metadata.CandidateGroups.Any(g => userGroups.Contains(g));
var isCandidate = isCandidateUser || isCandidateGroup;

bool assigneeSet = metadata.Assignee is not null;
bool candidatesSet = metadata.CandidateUsers.Count > 0 || metadata.CandidateGroups.Count > 0;

if (assigneeSet && candidatesSet)
{
    // Both constraints set — OR: satisfying either is sufficient (Camunda semantics)
    if (!isAssignee && !isCandidate)
        throw new BadRequestActivityException(
            $"User {userId} is neither the assignee ({metadata.Assignee}) nor in the candidate users/groups");
}
else if (assigneeSet)
{
    if (!isAssignee)
        throw new BadRequestActivityException(
            $"Task is assigned to {metadata.Assignee}, not {userId}");
}
else if (candidatesSet)
{
    if (!isCandidate)
        throw new BadRequestActivityException(
            $"User {userId} is not in the candidate users or candidate groups for this task");
}
// else: no constraints set — anyone can claim
```

The `InvalidOperationException` paths (activity not found, not active, not a user task) are **unchanged** and map to 409 Conflict in the controller.

### Logging

**`WorkflowInstance.Logging.cs`** — update `LogUserTaskClaimAttempt`:
```csharp
[LoggerMessage(EventId = <existing-id>, Level = LogLevel.Information,
    Message = "User task claim attempt: ActivityInstanceId={ActivityInstanceId}, UserId={UserId}, GroupCount={GroupCount}")]
private partial void LogUserTaskClaimAttempt(Guid activityInstanceId, string userId, int groupCount);
```
Call site in `WorkflowInstance.cs`: `LogUserTaskClaimAttempt(activityInstanceId, userId, userGroups.Count)`.

**`WorkflowCommandService`** — update `LogClaimingUserTask`:
```csharp
[LoggerMessage(EventId = <existing-id>, Level = LogLevel.Information,
    Message = "Claiming user task: WorkflowInstanceId={WorkflowInstanceId}, ActivityInstanceId={ActivityInstanceId}, UserId={UserId}, GroupCount={GroupCount}")]
private partial void LogClaimingUserTask(Guid workflowInstanceId, Guid activityInstanceId, string userId, int groupCount);
```
Call site: pass `userGroups.Count`.

## DTOs

| DTO | Change |
|---|---|
| `ClaimTaskRequest` | **Deleted** — no request body on claim endpoint (breaking change) |
| `CompleteTaskRequest` | Remove `UserId`, keep `Variables` (breaking change) |

## Testing

- `WorkflowExecution` unit tests: update `ClaimUserTask` calls to pass `userGroups`. Add:
  - User in a matching `CandidateGroup` can claim.
  - User NOT in `CandidateGroups` and not assignee/candidateUser cannot claim → `BadRequestActivityException`.
  - User in `CandidateUsers` can claim even when groups do not match (OR semantics).
  - User is the assignee AND candidate lists are also set → assignee can claim even if not a candidate; a candidate can claim even if not the assignee.
- `UserTaskIntegrationTests`: update grain-level `ClaimUserTask` calls to include `userGroups`.
- `WorkflowCommandServiceTests`: update `ClaimUserTask` calls; verify `userId` and `userGroups` are passed through correctly.
- Controller-level tests (if any): update claim endpoint (no body) and complete endpoint (no `UserId` in body).

## Manual Test Plan

Create `tests/manual/19-candidate-groups-claim/` with:
- BPMN fixture with a user task with `camunda:candidateGroups="managers"`.
- `test-plan.md` covering:
  1. Deploy with `FakeAuth:Groups` set to `[]` → attempt claim → expect 400.
  2. Set `FakeAuth:Groups: ["managers"]` in `appsettings.Development.json`, redeploy → claim → expect 200.
  3. Complete the claimed task → expect 200.

## Out of Scope

- Real authentication middleware.
- Group persistence or group management API.
- `UnclaimUserTask` — intentionally has no auth check (admin use case, unchanged).
