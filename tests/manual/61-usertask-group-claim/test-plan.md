# 61 — User-task claiming by user group

Verifies #588: a `<bpmn:userTask>` gated by `camunda:candidateGroups` (no assignee, no candidateUsers) is claimable only by callers whose request-supplied `userGroups` intersect the task's `CandidateGroups`. Pre-#588 the same task was silently claimable by any caller — the very gap this plan exists to regression-test.

## Prerequisites

- Aspire stack running (`dotnet run --project Fleans.Aspire` from `src/Fleans/`).
- Web UI reachable at `https://localhost:7124`; API at `https://localhost:7140`.
- Authentication NOT configured (default no-auth posture) — `Authentication:Authority` is absent. Auth-enabled JWT-derived groups are covered in a follow-up plan once a representative IdP fixture is available.

## Steps

### 1. Deploy fixture

```bash
curl -X POST 'https://localhost:7140/Definitions/deploy' \
  -H 'Content-Type: application/json' \
  --data-binary "@tests/manual/61-usertask-group-claim/group-claim.bpmn" -k
```

- [ ] Returns HTTP 200 with `processDefinitionKey = "group-claim"`.

### 2. Start one instance — happy-path claim

```bash
INSTANCE_A=$(curl -s -X POST 'https://localhost:7140/Execution/start' \
  -H 'Content-Type: application/json' \
  -d '{"WorkflowId":"group-claim"}' -k | jq -r '.workflowInstanceId')
echo "Instance A: $INSTANCE_A"
```

- [ ] Returns HTTP 200 with a `workflowInstanceId`.

Fetch the active user-task activityInstanceId:

```bash
TASK_A=$(curl -s 'https://localhost:7140/UserTasks?candidateGroup=managers' -k \
  | jq -r --arg wid "$INSTANCE_A" '.items[] | select(.workflowInstanceId == $wid) | .activityInstanceId')
echo "Task A: $TASK_A"
```

- [ ] Task appears in `GET /UserTasks?candidateGroup=managers` (read-side filter from #415 still works).

Claim with a group that intersects:

```bash
curl -X POST "https://localhost:7140/UserTasks/$TASK_A/claim" \
  -H 'Content-Type: application/json' \
  -d '{"UserId":"alice","UserGroups":["managers"]}' -k -i
```

- [ ] Returns HTTP 200.
- [ ] `GET /UserTasks/$TASK_A` shows the task's `assignee = "alice"` and `taskState = "Claimed"`.

### 3. Start a second instance — rejection path

```bash
INSTANCE_B=$(curl -s -X POST 'https://localhost:7140/Execution/start' \
  -H 'Content-Type: application/json' \
  -d '{"WorkflowId":"group-claim"}' -k | jq -r '.workflowInstanceId')
TASK_B=$(curl -s 'https://localhost:7140/UserTasks?candidateGroup=managers' -k \
  | jq -r --arg wid "$INSTANCE_B" '.items[] | select(.workflowInstanceId == $wid) | .activityInstanceId')
```

Claim with a group that does NOT intersect:

```bash
curl -X POST "https://localhost:7140/UserTasks/$TASK_B/claim" \
  -H 'Content-Type: application/json' \
  -d '{"UserId":"bob","UserGroups":["unrelated"]}' -k -i
```

- [ ] Returns HTTP 409 Conflict.
- [ ] Response body contains `"User bob is not authorized to claim this task"` (the consolidated, identifier-free rejection message — no leak of the task's candidate-group names).

### 4. Rejection log line

Inspect `fleans-core` logs:

```bash
docker compose logs fleans-core 2>&1 | grep -E "EventId.*1066|User task claim rejected"
```

- [ ] One log line per attempted-but-rejected claim, with `UserId={bob}` and `UserGroupCount=1` (the count of `userGroups` in the request).
- [ ] No group **names** appear in the log line (audit-side privacy guarantee — counts only).

### 5. Backward-compat: unrestricted task still claimable

Start a third instance against a separate fixture without group constraints (use the existing #18 fixture or any other unrestricted user-task BPMN). Claim with the legacy DTO shape (no `UserGroups` field):

```bash
curl -X POST "https://localhost:7140/UserTasks/$TASK_C/claim" \
  -H 'Content-Type: application/json' \
  -d '{"UserId":"alice"}' -k -i
```

- [ ] Returns HTTP 200 — wire-format backward compatibility holds for tasks without `CandidateGroups`.

## Pass criteria

All 5 checklists pass. A failure on Step 3 is the regression target for the silent-bypass case #588 closes — that failure would mean a group-restricted task is still claimable by anyone.

## Failure modes

- Step 2 task not found via `GET /UserTasks?candidateGroup=managers` → the BPMN parser may have lost the `camunda:` namespace; confirm the fixture declares `xmlns:camunda="http://camunda.org/schema/1.0/bpmn"` and the attribute is `camunda:candidateGroups` (NOT a child `<zeebe:assignmentDefinition>`). Anchor: `Fleans.Infrastructure/Bpmn/BpmnConverter.cs:314-315`.
- Step 3 returns HTTP 200 instead of 409 → the domain `Claim` rewrite at `UserTaskLifecycle.cs:22` is either missing the `CandidateGroups` branch or the controller is passing an empty `userGroups` to the grain regardless of body. Cross-reference the `IUserGroupResolver` registration in `Fleans.Api/Program.cs`.
- Step 4 log line absent → `[LoggerMessage(EventId = 1066)]` declaration in `WorkflowInstance.Logging.cs` not picked up by the source generator (re-run `dotnet build` against a clean obj/).
- Step 5 fails → backward-compat regression. The new `ClaimTaskRequest(string UserId, IReadOnlyList<string>? UserGroups = null)` DTO must keep the no-`UserGroups`-field wire shape working.
