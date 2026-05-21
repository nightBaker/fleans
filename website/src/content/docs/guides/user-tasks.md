---
title: User Tasks
description: How to model human-in-the-loop steps in Fleans — claim, complete, and unclaim user tasks via the REST API.
---

:::tip[When to reach for user tasks]
Reach for `<bpmn:userTask>` when a step needs a human decision before the workflow can advance — approvals, manual review, data-entry steps. For automated work (an API call, an email, a payment) use a [service task](/fleans/guides/service-tasks/) or a [custom-task plugin](/fleans/guides/writing-custom-tasks/) instead.
:::

> See also: the [User Task API reference](/fleans/reference/api/#user-task-endpoints) for exact wire shapes, status codes, and curl examples.

## What are user tasks?

A **user task** (`<bpmn:userTask>`) represents work a human operator must perform. The engine pauses the token at the task and exposes it to the outside world through `GET /UserTasks` so a UI (or any other client) can list, claim, and complete it. Once a human posts the result, the engine merges the result into the workflow scope and advances the token.

User tasks are the way Fleans models human-in-the-loop steps today. There is no built-in form renderer — the BPMN file describes *who* may act and *which* output variables are required, and your UI provides the form.

## How they work in Fleans

When a workflow instance reaches a `<bpmn:userTask>`, the engine:

1. Activates the task and writes a metadata entry into the active-task registry (queryable via `GET /UserTasks`).
2. Records the assignee, candidate users, candidate groups, and any required output variables (`<fleans:expectedOutputs>`) declared on the task.
3. Waits — no token movement happens until an external caller hits one of the lifecycle endpoints.
4. On `complete`, validates the caller and required outputs, merges the supplied variables into the enclosing scope, removes the task from the registry, and advances the token.

The lifecycle states are `Created → Claimed ⇆ Created → Claimed → Completed`, with claim/unclaim allowed to cycle until the task completes:

| From | Event | To | Caller check |
|---|---|---|---|
| Created | `POST /UserTasks/{id}/claim` (auth ok) | Claimed | yes — see [Who can claim?](#who-can-claim) |
| Claimed | `POST /UserTasks/{id}/claim` (different *or* same user, auth ok) | Claimed (overwritten) | yes — see [Who can claim?](#who-can-claim) — **no first-claim-wins; see [Limitations](#limitations--roadmap)** |
| Claimed | `POST /UserTasks/{id}/unclaim` | Created | **no** — any caller; see [Who can unclaim?](#who-can-unclaim) |
| Claimed | `POST /UserTasks/{id}/complete` (with `expectedOutputs`) | Completed | yes — caller must equal `claimedBy` |
| Completed | (terminal) | — | — |

> After a task reaches `Completed`, all lifecycle endpoints return **404** — the task is no longer in the active-task registry.

:::note[🔁 Re-claim is allowed]
A task can cycle `Created → Claimed → Created → Claimed → … → Completed`. The `complete` event removes the task from the `GET /UserTasks` registry; before that, a fresh `claim` is always possible. Combined with the lack of a state precondition on `claim` (see [Limitations](#limitations--roadmap)), this means even a task that is currently `Claimed` can be re-claimed by another authorized caller, silently overwriting the previous claim.
:::

## BPMN example

A minimal user task with required-output declarations:

```xml
<bpmn:userTask id="Approval" name="Approve request"
               camunda:assignee="alice">
  <bpmn:extensionElements>
    <fleans:expectedOutputs>
      <fleans:expectedOutput name="approved" />
      <fleans:expectedOutput name="reviewComment" />
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
| `camunda:candidateGroups` | ✅ enforced at claim | Caller's `userGroups` must contain at least one element (ordinal case-sensitive). Also filters `GET /UserTasks?candidateGroup=…`. See [User-group sourcing](#user-group-sourcing). |
| `<fleans:expectedOutputs>` | ✅ | Required output-variable contract enforced at `complete`. |
| `camunda:formKey` | ❌ | Parsed but ignored — no built-in form renderer. |
| `dueDate` | ❌ | Parsed but ignored — no SLA / escalation timer wiring today. |
| `zeebe:assignmentDefinition` | ❌ | Parsed but ignored — use `camunda:*` attributes instead. |

## Authentication mode

The examples in the rest of this guide target the default unauthenticated profile (no `Authentication:Schemes` configuration on the API). When you turn on JWT bearer auth (see [API JWT Authentication](/fleans/reference/api/#authentication)), every user-task endpoint returns **401 Unauthorized** before the documented 400 / 404 / 409 paths can fire. Add `Authorization: Bearer <token>` to each `curl` below, or expect 401.

## Discovering pending tasks

:::note[Endpoint location]
User-task endpoints live under `/UserTasks/...` (since [PR #614](https://github.com/nightBaker/fleans/pull/614)). Older guides and BPMN-tool integrations may reference `/Workflow/tasks/...` — that route is gone; replace with `/UserTasks/...`.
:::

`GET /UserTasks` returns the paginated list of active user tasks across all workflow instances:

```bash
curl 'https://localhost:7140/UserTasks?page=1&pageSize=20'
```

Filter by assignee or candidate group when you need a per-user or per-group inbox:

```bash
curl 'https://localhost:7140/UserTasks?assignee=alice'
curl 'https://localhost:7140/UserTasks?candidateGroup=managers'
```

Each item carries the `activityInstanceId` (a GUID) you need for the lifecycle endpoints below. Filtering happens server-side — the response shape is the same as the unfiltered list.

:::note[GET filter is advisory; claim path enforces]
The `candidateGroup=` query parameter only filters which tasks are *returned* — it's a UI-side inbox affordance. The `claim` path itself enforces group membership server-side (as of #588 / PR #619): the caller's `userGroups` must intersect the task's `candidateGroups`. See [User-group sourcing](#user-group-sourcing) below for how `userGroups` is sourced under no-auth vs JWT-auth deployments.
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
| `camunda:candidateGroups="managers"` only | caller's `userGroups` must contain at least one element of the task's `candidateGroups` (ordinal case-sensitive; see [User-group sourcing](#user-group-sourcing) below) |

Successful claim:

```bash
curl -X POST https://localhost:7140/UserTasks/{activityInstanceId}/claim \
  -H "Content-Type: application/json" \
  -d '{"UserId": "alice"}'
# 200 OK
```

Unauthorized caller (a task whose `candidateUsers` is `alice,bob`, claimed by `charlie`):

```
HTTP/1.1 409 Conflict
{ "error": "User charlie is not in candidate users list" }
```

For the `assignee`-only branch, the body is `"Task is assigned to alice, not charlie"`. Exact wording is engine-controlled — `Fleans.Domain/Aggregates/Services/UserTaskLifecycle.cs` is the canonical source.

#### User-group sourcing

The caller's `userGroups` list — used to evaluate the `candidateGroups` row of the Who-can-claim table — is sourced from one of two places depending on deployment mode:

- **No-auth deployments** (default — no `Authentication:Schemes` configured) — the `userGroups: string[]` field of the `POST /UserTasks/{id}/claim` request body. Trusted on the wire; suitable only for trusted-network deployments.
- **JWT-auth deployments** — the `groups` claim of the bearer token, extracted by `JwtUserGroupResolver`. The body's `userGroups` field is **ignored** in this mode — the token is the only source. This prevents callers from spoofing group membership.

Comparison is `StringComparer.Ordinal` — case-sensitive byte-for-byte, the same as .NET's `StringComparison.Ordinal`. `"Managers"` and `"managers"` are different groups.

A claim that doesn't satisfy any of the three OR-constraints (assignee match, candidate-users contains, or candidate-groups intersect) returns:

- **HTTP 409 Conflict** with body `{ "error": "User <userId> is not authorized to claim this task" }`. The message includes the caller's `userId` (for caller-side debugging) but **does not enumerate the task's `Assignee` or candidate sets** — that detail goes to the structured log instead.
- **Structured log entry** at `Warning` level, EventId 1066, carrying `UserId=<userId>` and `UserGroupCount=<count>` only — never the group names or any of the task's candidate sets.

:::note[Historical behaviour]
Before PR #619 (v0.5.0+), `candidateGroups` was filter-only on `GET /UserTasks?candidateGroup=…` — a non-member could still claim a task by guessing its `activityInstanceId`. Upgrade to v0.5.0+ for strict enforcement.
:::

#### Who can unclaim?

> **⚠️ Unclaim is not authorization-gated.** `POST /UserTasks/{id}/unclaim` accepts any caller and resets the task to `Created`. The caller does not need to be the original `claimedBy` user, the assignee, or a member of any candidate group / list. After unclaim, the task is up for grabs again — anyone within the [Who can claim?](#who-can-claim) rules can re-claim it.
>
> **Where to enforce.** Gate unclaim in your front-end (only show the "Release task" button to the user who currently owns the claim) and / or in an API gateway in front of the Fleans API. Do not rely on the engine to refuse cross-user unclaims.

> **Unclaim accepts any state.** Calling `POST /UserTasks/{id}/unclaim` on a task that is already `Created` returns 200 with no observable change — the engine has no `state == Claimed` precondition on unclaim. UI surfaces should hide the "Release task" button when the task is unclaimed, but the API will not reject the call. Calling unclaim on a `Completed` task returns 404 (the task is no longer in the active registry).

### Required outputs (`expectedOutputs`)

When the task declares `<fleans:expectedOutputs>`, the `complete` request body **must** supply every named variable. Missing variables fail validation with 409:

```bash
curl -X POST https://localhost:7140/UserTasks/{activityInstanceId}/complete \
  -H "Content-Type: application/json" \
  -d '{"UserId": "alice", "Variables": {"approved": true}}'
# 409 Conflict
# { "error": "Missing required output variables: reviewComment" }
```

Supplying every required variable produces a 200 and advances the token:

```bash
curl -X POST https://localhost:7140/UserTasks/{activityInstanceId}/complete \
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
      <fleans:expectedOutput name="approved" />
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

User tasks expose two endpoints for declaring failure from outside the diagram, plus three BPMN-side patterns for modelling it inside the diagram.

### Programmatic failure: `POST /UserTasks/{id}/fail`

Marks the user-task activity as `ActivityFailed` with a typed error code/message; routes through the host's Error Boundary Event if present, otherwise fails the workflow instance.

```bash
curl -X POST https://localhost:7140/UserTasks/{activityInstanceId}/fail \
  -H "Content-Type: application/json" \
  -d '{"UserId": "alice", "ErrorCode": "rejected", "ErrorMessage": "Customer cancelled"}'
# 200 OK
```

- **Idempotent.** Double-call returns 200.
- **400** if `ErrorMessage` is missing or empty.
- **404** on unknown task.

Regression home: [`tests/manual/45-user-task-fail-cancel/test-plan.md`](https://github.com/nightBaker/fleans/blob/main/tests/manual/45-user-task-fail-cancel/test-plan.md).

### Cancellation: `POST /UserTasks/{id}/cancel`

Marks the user-task activity terminal with `ActivityCancelled` and cleans up the active-task registry entry. Unlike `/fail`, no error boundary fires — cancellation is the "stop without surfacing as a fault" path, suitable for "user is no longer needed" scenarios where workflow logic should continue regardless.

```bash
curl -X POST https://localhost:7140/UserTasks/{activityInstanceId}/cancel \
  -H "Content-Type: application/json" \
  -d '{"UserId": "alice"}'
# 200 OK
```

- **Body is optional** — `{"UserId": "..."}` is captured for audit but not required.
- **Idempotent.** Double-call returns 200.
- **404** on unknown task.

### BPMN-side declarative alternatives

For modelling failure inside the diagram (rather than from outside the engine), use one of:

1. **Conditional gateway after the task** — let the user task complete normally, then branch on its output variables (e.g., `_context.approved`) via an exclusive gateway. This is the simplest pattern for "approved vs rejected" routing and is what most decision-style user tasks need.
2. **Sub-process + error boundary** — wrap the user task and a downstream script task inside a `<bpmn:subProcess>`, have the script task throw when the decision indicates failure, and attach an `errorBoundaryEvent` to the **sub-process** (not the user task — boundary events catch only errors raised by the activity they are attached to). The handler flow then runs the recovery path.
3. **Timer / escalation boundary on the user task** — for SLA breaches (a human is taking too long to act), attach an interrupting timer or escalation boundary to the user task itself. The boundary cancels the user task and routes to a handler. Combine with a non-interrupting timer first if you want a warning before the cut-off.

The full error-handling pattern is the subject of the [Error Handling guide (#394)](https://github.com/nightBaker/fleans/issues/394). For the concept-level overview of how service-task and custom-task plugins compare to user tasks, see the [Custom Tasks concept page](/fleans/concepts/custom-tasks/).

## Limitations & roadmap

- **Several attributes are parsed but not honoured today.** See the *Recognised attributes* table above for the complete list (currently `camunda:formKey`, `dueDate`, and the Camunda-export `zeebe:assignmentDefinition`). Each is read by the parser but has no engine wiring; document them in your BPMN for future-tool readers but do not rely on them at runtime.
- **Unclaim accepts any caller.** Enforce ownership in your front-end / gateway. Do not rely on the engine to refuse cross-user unclaims.
- **Claim is not exclusive.** Any caller authorized per [Who can claim?](#who-can-claim) can `claim` a task that is already in the `Claimed` state; the previous `claimedBy` is silently overwritten without a 409 or a domain event distinguishing it from a fresh claim. If your workflow needs first-claim-wins semantics, enforce it in the front-end / API gateway by gating `claim` calls on `task.state == "Created"` from the read model.
- **No first-class reassignment endpoint.** Use `unclaim` followed by `claim` from the new owner — but remember unclaim is unauthenticated at the engine level.
- **No built-in form rendering.** `formKey` is parsed but never consulted. Your UI provides the form; the engine only enforces `expectedOutputs`.

## Best practices

- **Pair `assignee` with `candidateUsers` for fall-through.** A primary owner plus a backup pool keeps the task moveable when the primary is unavailable.
- **Idempotent UI.** A user clicking "Submit" twice should be safe; the second call returns 409 from the engine, but your UI should not double-post side effects.
- **Surface `activityInstanceId` to consumers.** It is the only key the lifecycle endpoints accept — your task-list UI and any worker that wants to programmatically nudge a task must persist it.
- **Batch polling at the `Read` rate-limit budget.** `GET /UserTasks` is on the `Read` policy; bulk-polling clients should respect that budget rather than hammering per-user endpoints.
- **Validate on the server.** `expectedOutputs` is the engine's contract; do not skip it just because your UI also validates — a stray API client should not be able to complete a task without the required variables.
