---
title: User Tasks
description: How to model human-in-the-loop steps in Fleans — claim, complete, and unclaim user tasks via the REST API.
---

:::tip[When to reach for user tasks]
Reach for `<bpmn:userTask>` when a step needs a human decision before the workflow can advance — approvals, manual review, data-entry steps. For automated work (an API call, an email, a payment) use a [service task](/fleans/guides/service-tasks/) or a [custom-task plugin](/fleans/guides/writing-custom-tasks/) instead.
:::

## What are user tasks?

A **user task** (`<bpmn:userTask>`) represents work a human operator must perform. The engine pauses the token at the task and exposes it to the outside world through `GET /Workflow/tasks` so a UI (or any other client) can list, claim, and complete it. Once a human posts the result, the engine merges the result into the workflow scope and advances the token.

User tasks are the way Fleans models human-in-the-loop steps today. There is no built-in form renderer — the BPMN file describes *who* may act and *which* output variables are required, and your UI provides the form.

## How they work in Fleans

When a workflow instance reaches a `<bpmn:userTask>`, the engine:

1. Activates the task and writes a metadata entry into the active-task registry (queryable via `GET /Workflow/tasks`).
2. Records the assignee, candidate users, candidate groups, and any required output variables (`<fleans:expectedOutputs>`) declared on the task.
3. Waits — no token movement happens until an external caller hits one of the lifecycle endpoints.
4. On `complete`, validates the caller and required outputs, merges the supplied variables into the enclosing scope, removes the task from the registry, and advances the token.

The lifecycle states are `Created → Claimed ⇆ Created → Claimed → Completed`, with claim/unclaim allowed to cycle until the task completes:

| From | Event | To | Caller check |
|---|---|---|---|
| Created | `POST /tasks/{id}/claim` (auth ok) | Claimed | yes — see [Who can claim?](#who-can-claim) |
| Claimed | `POST /tasks/{id}/claim` (different *or* same user, auth ok) | Claimed (overwritten) | yes — see [Who can claim?](#who-can-claim) — **no first-claim-wins; see [Limitations](#limitations--roadmap)** |
| Claimed | `POST /tasks/{id}/unclaim` | Created | **no** — any caller; see [Who can unclaim?](#who-can-unclaim) |
| Claimed | `POST /tasks/{id}/complete` (with `expectedOutputs`) | Completed | yes — caller must equal `claimedBy` |
| Completed | (terminal) | — | — |

> After a task reaches `Completed`, all lifecycle endpoints return **404** — the task is no longer in the active-task registry.

:::note[🔁 Re-claim is allowed]
A task can cycle `Created → Claimed → Created → Claimed → … → Completed`. The `complete` event removes the task from the `GET /Workflow/tasks` registry; before that, a fresh `claim` is always possible. Combined with the lack of a state precondition on `claim` (see [Limitations](#limitations--roadmap)), this means even a task that is currently `Claimed` can be re-claimed by another authorized caller, silently overwriting the previous claim.
:::

## BPMN example

A minimal user task with required-output declarations:

```xml
<bpmn:userTask id="Approval" name="Approve request"
               camunda:assignee="alice">
  <bpmn:extensionElements>
    <fleans:expectedOutputs>
      <fleans:output name="approved" />
      <fleans:output name="reviewComment" />
    </fleans:expectedOutputs>
  </bpmn:extensionElements>
</bpmn:userTask>
```

For a worked end-to-end example with a downstream script task that consumes the user-task outputs, see [Output variables and downstream consumption](#output-variables-and-downstream-consumption) below.

### Recognised attributes

| Attribute | Honoured? | Notes |
|---|---|---|
| `camunda:assignee` | ✅ | Single user id; checked at `claim`. |
| `camunda:candidateUsers` | ✅ | Comma-separated user list; checked at `claim`. |
| `camunda:candidateGroups` | ⚠️ filter-only | Used to filter `GET /Workflow/tasks?candidateGroup=…`, **not** enforced at `claim`. |
| `<fleans:expectedOutputs>` | ✅ | Required output-variable contract enforced at `complete`. |
| `camunda:formKey` | ❌ | Parsed but ignored — no built-in form renderer. |
| `dueDate` | ❌ | Parsed but ignored — no SLA / escalation timer wiring today. |
| `zeebe:assignmentDefinition` | ❌ | Parsed but ignored — use `camunda:*` attributes instead. |

## Authentication mode

The examples in the rest of this guide target the default unauthenticated profile (no `Authentication:Schemes` configuration on the API). When you turn on JWT bearer auth (see [API JWT Authentication](/fleans/reference/api/#authentication)), every user-task endpoint returns **401 Unauthorized** before the documented 400 / 404 / 409 paths can fire. Add `Authorization: Bearer <token>` to each `curl` below, or expect 401.

## Discovering pending tasks

`GET /Workflow/tasks` returns the paginated list of active user tasks across all workflow instances:

```bash
curl 'https://localhost:7140/Workflow/tasks?page=1&pageSize=20'
```

Filter by assignee or candidate group when you need a per-user or per-group inbox:

```bash
curl 'https://localhost:7140/Workflow/tasks?assignee=alice'
curl 'https://localhost:7140/Workflow/tasks?candidateGroup=managers'
```

Each item carries the `activityInstanceId` (a GUID) you need for the lifecycle endpoints below. Filtering happens server-side — the response shape is the same as the unfiltered list.

:::caution[`candidateGroup=` is advisory]
The `candidateGroup` query parameter filters which tasks are returned, but it does **not** prevent a non-member from calling `claim` against an `activityInstanceId` they discovered some other way. Group enforcement is a UI / gateway concern today.
:::

## Claim, complete, unclaim

These three endpoints make up the user-task lifecycle.

### Who can claim?

The authorization rule on `claim` is determined by which BPMN attributes are set:

| BPMN attributes set | Caller `UserId` accepted when… |
|---|---|
| **(none set)** | **anyone can claim** — the engine has no constraint to enforce |
| `camunda:assignee="alice"` only | `UserId == "alice"` |
| `camunda:candidateUsers="alice,bob"` only | `UserId ∈ {alice, bob}` |
| Both `assignee="alice"` and `candidateUsers="bob,carol"` | `UserId == "alice"` **OR** `UserId ∈ {bob, carol}` (OR-logic) |
| `camunda:candidateGroups="managers"` only | **anyone can claim** — group membership is not enforced (see [Who can unclaim?](#who-can-unclaim) and [Limitations](#limitations--roadmap)) |

Successful claim:

```bash
curl -X POST https://localhost:7140/Workflow/tasks/{activityInstanceId}/claim \
  -H "Content-Type: application/json" \
  -d '{"UserId": "alice"}'
# 200 OK
```

Unauthorized caller (a task whose `candidateUsers` is `alice,bob`, claimed by `charlie`):

```
HTTP/1.1 409 Conflict
{ "message": "User charlie is not in candidate users list" }
```

For the `assignee`-only branch, the body is `"Task is assigned to alice, not charlie"`. Exact wording is engine-controlled — `Fleans.Domain/Aggregates/Services/UserTaskLifecycle.cs` is the canonical source.

#### Who can unclaim?

> **⚠️ Unclaim is not authorization-gated.** `POST /Workflow/tasks/{id}/unclaim` accepts any caller and resets the task to `Created`. The caller does not need to be the original `claimedBy` user, the assignee, or a member of any candidate group / list. After unclaim, the task is up for grabs again — anyone within the [Who can claim?](#who-can-claim) rules can re-claim it.
>
> **Where to enforce.** Gate unclaim in your front-end (only show the "Release task" button to the user who currently owns the claim) and / or in an API gateway in front of the Fleans API. Do not rely on the engine to refuse cross-user unclaims.

> **Unclaim accepts any state.** Calling `POST /Workflow/tasks/{id}/unclaim` on a task that is already `Created` returns 200 with no observable change — the engine has no `state == Claimed` precondition on unclaim. UI surfaces should hide the "Release task" button when the task is unclaimed, but the API will not reject the call. Calling unclaim on a `Completed` task returns 404 (the task is no longer in the active registry).

### Required outputs (`expectedOutputs`)

When the task declares `<fleans:expectedOutputs>`, the `complete` request body **must** supply every named variable. Missing variables fail validation with 409:

```bash
curl -X POST https://localhost:7140/Workflow/tasks/{activityInstanceId}/complete \
  -H "Content-Type: application/json" \
  -d '{"UserId": "alice", "Variables": {"approved": true}}'
# 409 Conflict
# { "message": "Missing required output variables: reviewComment" }
```

Supplying every required variable produces a 200 and advances the token:

```bash
curl -X POST https://localhost:7140/Workflow/tasks/{activityInstanceId}/complete \
  -H "Content-Type: application/json" \
  -d '{"UserId": "alice", "Variables": {"approved": true, "reviewComment": "Looks good"}}'
# 200 OK
```

The caller must equal the current `claimedBy`; a mismatch is also 409 (`"Task is claimed by alice, not bob"`).

### Output variables and downstream consumption

Variables passed to `complete` are merged into the enclosing variable scope before the next activity runs — the same merge semantics as `<bpmn:task>` and `<bpmn:scriptTask>`. A downstream script task can therefore consume them by name:

```xml
<bpmn:userTask id="Approval" name="Approve request">
  <bpmn:extensionElements>
    <fleans:expectedOutputs>
      <fleans:output name="approved" />
    </fleans:expectedOutputs>
  </bpmn:extensionElements>
</bpmn:userTask>

<bpmn:scriptTask id="LogDecision" scriptFormat="csharp">
  <bpmn:script>_context.outcome = _context.approved ? "approved" : "rejected";</bpmn:script>
</bpmn:scriptTask>
```

After `complete` posts `{"Variables": {"approved": true}}`, `LogDecision` sees `_context.approved == true` and writes `_context.outcome = "approved"`. Use `_context.<name>` (ExpandoObject dot-notation), not `_context["<name>"]`, to match the engine's canonical idiom (see `tests/manual/02-script-tasks/script-variable-manipulation.bpmn`).

> **No partial merge on validation failure.** When `complete` is called with a missing required output (e.g., `expectedOutputs` declares `approved` *and* `reviewComment` but the body only supplies `approved`), `ValidateAndPrepareCompletion` throws *before* the workflow's variable scope is touched and the controller returns 409. The supplied variables are not partially merged — the next activity sees the same scope it saw before the failed call.

## Error handling

There is **no `/fail-activity` endpoint** for user tasks (or for service tasks — see the [Service Tasks guide](/fleans/guides/service-tasks/)). Programmatic failure is not exposed as an HTTP verb. To surface a failure path from a user task, use one of:

1. **Conditional gateway after the task** — let the user task complete normally, then branch on its output variables (e.g., `_context.approved`) via an exclusive gateway. This is the simplest pattern for "approved vs rejected" routing and is what most decision-style user tasks need.
2. **Sub-process + error boundary** — wrap the user task and a downstream script task inside a `<bpmn:subProcess>`, have the script task throw when the decision indicates failure, and attach an `errorBoundaryEvent` to the **sub-process** (not the user task — boundary events catch only errors raised by the activity they are attached to). The handler flow then runs the recovery path.
3. **Timer / escalation boundary on the user task** — for SLA breaches (a human is taking too long to act), attach an interrupting timer or escalation boundary to the user task itself. The boundary cancels the user task and routes to a handler. Combine with a non-interrupting timer first if you want a warning before the cut-off.

The full error-handling pattern is the subject of the [Error Handling guide (#394)](https://github.com/nightBaker/fleans/issues/394).

## Limitations & roadmap

- **`camunda:formKey`, `dueDate`, `zeebe:assignmentDefinition` not honoured today.** Each is parsed by `BpmnConverter.cs` but has no engine wiring; document them in your BPMN for future-tool readers but do not rely on them at runtime.
- **`candidateGroups` is advisory (filter-only).** It scopes the `GET /Workflow/tasks?candidateGroup=…` response so a UI can show the right inbox, but it does **not** prevent a non-member from claiming a task they got the `activityInstanceId` for some other way. Enforce group membership at the front-end or gateway.
- **Unclaim accepts any caller.** Enforce ownership in your front-end / gateway. Do not rely on the engine to refuse cross-user unclaims.
- **Claim is not exclusive.** Any caller authorized per [Who can claim?](#who-can-claim) can `claim` a task that is already in the `Claimed` state; the previous `claimedBy` is silently overwritten without a 409 or a domain event distinguishing it from a fresh claim. If your workflow needs first-claim-wins semantics, enforce it in the front-end / API gateway by gating `claim` calls on `task.state == "Created"` from the read model.
- **No first-class reassignment endpoint.** Use `unclaim` followed by `claim` from the new owner — but remember unclaim is unauthenticated at the engine level.
- **No built-in form rendering.** `formKey` is parsed but never consulted. Your UI provides the form; the engine only enforces `expectedOutputs`.

## Best practices

- **Pair `assignee` with `candidateUsers` for fall-through.** A primary owner plus a backup pool keeps the task moveable when the primary is unavailable.
- **Idempotent UI.** A user clicking "Submit" twice should be safe; the second call returns 409 from the engine, but your UI should not double-post side effects.
- **Surface `activityInstanceId` to consumers.** It is the only key the lifecycle endpoints accept — your task-list UI and any worker that wants to programmatically nudge a task must persist it.
- **Batch polling at the `Read` rate-limit budget.** `GET /Workflow/tasks` is on the `Read` policy; bulk-polling clients should respect that budget rather than hammering per-user endpoints.
- **Validate on the server.** `expectedOutputs` is the engine's contract; do not skip it just because your UI also validates — a stray API client should not be able to complete a task without the required variables.
