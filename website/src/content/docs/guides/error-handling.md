---
title: Error Handling
description: How to model structured failure recovery in Fleans — BPMN error boundary events, escalations, and compensation handlers, with worked examples.
---

<!--
  DRIFT-GUARD: cited line numbers verified at branch SHA e7f3762
  - src/Fleans/Fleans.Domain/Errors/BadRequestActivityException.cs:5-13 (400 mapping)
  - src/Fleans/Fleans.Domain/Errors/CustomTaskFailedActivityException.cs:5-22 (caller-supplied code)
  - src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs:723-784 (AdvanceCompensationWalkIfHandlerCompleted; VariablesMerged emit at line 767)
  - src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs:132 (errorEventDefinition on startEvent — event sub-process trigger)
  - src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs:209-269 (intermediateThrowEvent + endEvent: compensate/escalation parse)
  - src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs:665-710 (boundaryEvent: compensate/error/escalation parse)
  - src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs:759-815 (compensation validation: at-most-one boundary, handler not in flow, compensable activityRef)
  - src/Fleans/Fleans.Infrastructure/Scripts/DynamicExpressoScriptExpressionExecutor.cs:46 (interpreter.SetVariable("_context", variables))
  - tests/manual/11-error-boundary/{child-that-fails.bpmn,error-on-call-activity.bpmn}
  - tests/manual/19-event-subprocess-error/error-event-subprocess.bpmn
  - tests/manual/24-escalation-event/{child-escalation-end,child-escalation-throw,parent-escalation-interrupting,parent-escalation-non-interrupting}.bpmn
  - tests/manual/24-compensation-event/compensation-broadcast.bpmn
  Re-verify on every edit.
-->

Workflows fail. External calls time out, data is invalid, business rules are violated.
Fleans implements three orthogonal BPMN mechanisms for structured failure recovery —
**errors**, **escalations**, and **compensation** — each suited to a different shape of problem.

This guide is a developer's tour of all three, anchored to the runnable fixtures
under `tests/manual/`. For the underlying parsing rules, the canonical reference is
the [BPMN Support](/fleans/concepts/bpmn-support/) page.

## When to use what

| Situation | Mechanism | Catches | Side-effect semantics |
| --- | --- | --- | --- |
| Recoverable failure inside a task (validation, timeout, 5xx response) | **Error** | Error boundary on activity / error event sub-process | Activity ends in `Failed`; execution routes through the boundary's outgoing flow |
| Out-of-band domain signal (SLA breach, manager-needs-to-know, "still continuing") | **Escalation** | Escalation boundary on `SubProcess` / `CallActivity`, or escalation event sub-process | Interrupting boundary cancels the host activity; non-interrupting runs the handler in parallel and the host continues |
| Reverse a successfully-completed activity (cancel a reservation that was made) | **Compensation** | Compensation boundary on the original activity | Reverse-order handler walk; each handler runs in an isolated child scope seeded with the compensable activity's completion-time variables |

The decision matrix:

- The activity *failed* and you want recovery → **Error**.
- The activity *succeeded* but a downstream step needs to undo it → **Compensation**.
- The activity is *still running* (or completed normally) but a sibling needs to know
  something out-of-the-ordinary happened → **Escalation**.

## Error events

Use error events whenever an activity can fail in a way the workflow can recover from.
Fleans supports the three standard BPMN forms:

1. **Error boundary event** attached to a `serviceTask` / `scriptTask` / `userTask` / `callActivity`.
2. **Error event sub-process** (`<subProcess triggeredByEvent="true">` containing
   `<startEvent><errorEventDefinition/></startEvent>`).
3. **Error end event** thrown from inside a sub-process to be caught by a parent boundary.

### Error boundary on a service task

```xml
<bpmn:serviceTask id="charge-payment" name="Charge Payment" />
<bpmn:boundaryEvent id="payment-failed" attachedToRef="charge-payment">
  <bpmn:errorEventDefinition errorRef="PaymentError" />
</bpmn:boundaryEvent>
<bpmn:error id="PaymentError" errorCode="400" />
```

The `errorRef` resolves to a `<bpmn:error errorCode="…">` declaration. If the
boundary omits `errorRef` entirely, it acts as a **catch-all** — any error from
the host activity routes through it. Specific-code matches always take priority
over catch-all in the order of declaration.

Per BPMN spec, error boundaries are **always interrupting** — Fleans rejects
`cancelActivity="false"` on error boundaries, and the management UI's editor
disables the *Interrupting* checkbox for them.

Full fixture: `tests/manual/11-error-boundary/error-on-call-activity.bpmn` —
deploy steps in `tests/manual/11-error-boundary/test-plan.md`.

:::caution[Known limitation: child-process errors don't bubble to parent CallActivity]
Per `tests/manual/11-error-boundary/test-plan.md`, **child-process errors do not
currently propagate to a parent `CallActivity`'s error boundary**. The boundary stays
armed but never fires; the CallActivity stays in `Running` state.

**Workaround:** catch the error inside the child scope using an *error event
sub-process* (or a boundary event on a sub-process inside the child), and exit
the child cleanly. The parent's call activity will then complete normally, and
you can branch on a variable the child set to indicate the recovery path.

This is regression item #11 in the manual-test list (`KNOWN BUG` until resolved).
:::

### Error event sub-process

Catches an error thrown anywhere inside the enclosing scope, not just on a single
activity:

```xml
<bpmn:subProcess id="order-flow">
  <!-- normal flow … -->
  <bpmn:subProcess id="error-handler" triggeredByEvent="true">
    <bpmn:startEvent id="caught">
      <bpmn:errorEventDefinition errorRef="OrderError" />
    </bpmn:startEvent>
    <!-- handler activities … -->
  </bpmn:subProcess>
</bpmn:subProcess>
```

Error event sub-processes are always interrupting per BPMN 2.0 §10.2.4 — they
cancel sibling activities in the enclosing scope when fired. A `BoundaryErrorEvent`
on the failing activity takes precedence over an error event sub-process.

Full fixture: `tests/manual/19-event-subprocess-error/error-event-subprocess.bpmn`.

## Throwing errors from script and custom tasks

There is **no `/fail-activity` REST endpoint**. The way an activity fails is by
throwing a typed exception from inside its handler. Three patterns:

### From a script task — `BadRequestActivityException`

Use this when input is invalid or a business rule is violated. It maps to error
code **`"400"`**:

```xml
<bpmn:scriptTask id="validate" scriptFormat="csharp">
  <bpmn:script>
    if ((int)_context.amount > 10000)
        throw new BadRequestActivityException("amount exceeds limit");
    _context.validated = true;
  </bpmn:script>
</bpmn:scriptTask>
```

The `_context` variable is the script-task variable scope (see the
[BPMN Support — Script Tasks](/fleans/concepts/bpmn-support/) section).
Any error boundary on the script task with `errorRef` pointing at a `<bpmn:error errorCode="400"/>`
will catch this.

### From a custom-task plugin — `CustomTaskFailedActivityException`

Plugin authors throw this when their integration fails (HTTP 5xx, third-party
rejection, etc.) and supply their own error code:

```csharp
public override async Task<IDictionary<string, object?>> ExecuteAsync(
    IReadOnlyDictionary<string, object?> inputs, CancellationToken ct)
{
    var response = await _http.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
        throw new CustomTaskFailedActivityException(
            code: ((int)response.StatusCode).ToString(),
            message: $"Upstream returned {response.StatusCode}");
    // …
}
```

The error code is whatever the plugin passes, so error boundaries can match on
specific HTTP statuses (`"404"`, `"504"`, etc.). See `tests/manual/39-rest-caller/`
for an end-to-end worked example using the REST Caller plugin.

### Anything else — generic `Exception`

If a script or plugin throws *any other* exception, the activity fails with code
**`"500"`** and the exception's `Message`. Use this only when you can't classify
the failure — prefer the typed exceptions above.

## Escalation

Escalations express *something noteworthy that isn't strictly a failure*. The
canonical example: an SLA timer fires, but the work is still in progress and we
want to alert ops without cancelling the activity.

### Interrupting vs non-interrupting boundary

- `cancelActivity="true"` (default): cancels the host SubProcess/CallActivity and
  routes through the boundary.
- `cancelActivity="false"`: runs the handler in parallel; the host activity keeps
  running.

```xml
<bpmn:boundaryEvent id="sla-breach" attachedToRef="approval-process"
                    cancelActivity="false">
  <bpmn:escalationEventDefinition escalationRef="SlaBreach" />
</bpmn:boundaryEvent>
<bpmn:escalation id="SlaBreach" escalationCode="SLA_BREACH" />
```

Escalation boundaries may **only** be attached to a `SubProcess` or `CallActivity`
— Fleans rejects them on regular tasks at parse time.

### Throw events

Inside a sub-process you can throw an escalation either at the end (terminates
the sub-process) or mid-flow (continues execution):

```xml
<bpmn:intermediateThrowEvent id="warn">
  <bpmn:escalationEventDefinition escalationRef="SlaBreach" />
</bpmn:intermediateThrowEvent>
```

### Specific code vs catch-all

Specific `escalationCode` matches always take priority over a catch-all
(no `escalationRef`) boundary. Per BPMN spec, **uncaught escalations are
non-faulting** — the workflow continues normally even if no boundary catches.

Full fixtures: `tests/manual/24-escalation-event/` —
- `parent-escalation-interrupting.bpmn` — interrupting boundary on a CallActivity.
- `parent-escalation-non-interrupting.bpmn` — non-interrupting variant.
- `child-escalation-throw.bpmn`, `child-escalation-end.bpmn` — child processes
  throwing the escalation that the parent's boundary catches.

## Compensation

Compensation reverses *successfully-completed* activities. Classic use case:
a booking workflow completes "reserve flight", "reserve hotel", "charge card";
charging fails, and you need to cancel the hotel and flight in reverse order.

### Boundary + handler

```xml
<bpmn:scriptTask id="reserve-hotel" scriptFormat="csharp"> … </bpmn:scriptTask>
<bpmn:boundaryEvent id="comp-hotel" attachedToRef="reserve-hotel">
  <bpmn:compensateEventDefinition />
</bpmn:boundaryEvent>
<bpmn:scriptTask id="cancel-hotel" scriptFormat="csharp" isForCompensation="true"> … </bpmn:scriptTask>
<bpmn:association sourceRef="comp-hotel" targetRef="cancel-hotel" />
```

Two parsing rules Fleans enforces (see `BpmnConverter.cs:759-815`):

1. **At most one** compensation boundary per compensable activity.
2. The handler activity must have **no incoming sequence flow** — handlers are
   detached, only invoked during a compensation walk.

### Throw to trigger

```xml
<!-- Broadcast: compensate every compensable activity in reverse order -->
<bpmn:intermediateThrowEvent id="rollback">
  <bpmn:compensateEventDefinition />
</bpmn:intermediateThrowEvent>

<!-- Targeted: compensate just one specific activity -->
<bpmn:intermediateThrowEvent id="rollback-hotel">
  <bpmn:compensateEventDefinition activityRef="reserve-hotel" />
</bpmn:intermediateThrowEvent>
```

Or end the process with a `<bpmn:endEvent><bpmn:compensateEventDefinition/></bpmn:endEvent>`
inside an error sub-process to compensate-then-terminate.

:::caution[Variable-scope invariant — read this before writing handlers]
Each compensation handler runs in an **isolated child scope** seeded with the
compensable activity's completion-time variable snapshot (overlaid on the
enclosing scope). After a handler completes successfully, its variable changes
are merged back into the enclosing scope **before the next handler spawns**.

This guarantees:

- Later handlers see the cumulative effect of earlier handlers (no stale reads).
- Side-effects from compensation handlers are not lost after the walk finishes.

Implementation: `WorkflowExecution.AdvanceCompensationWalkIfHandlerCompleted`
(at `WorkflowExecution.cs:723-784`) emits a `VariablesMerged` event with the
handler's full variable map targeting the parent scope's variable ID
(line 767). If you find yourself refactoring the compensation path, do not
break this invariant — it is also documented as a hard rule in the project's
`CLAUDE.md`.
:::

Full fixture: `tests/manual/24-compensation-event/compensation-broadcast.bpmn` —
verifies reverse-order execution (`cancel_flight` runs before `cancel_hotel`)
and that compensation handlers can mutate variables.

## Best-practice patterns

A short cookbook for the common shapes:

### Wrap risky external calls in CallActivity + error boundary

When a service task calls an external system you do not control, wrap it in a
CallActivity (or sub-process) and attach the error boundary to *that*, not to
the service task itself. This gives you a clean recovery path that can include
retry logic, alternative endpoints, or operator-driven user tasks.

### Compensation for reservation rollback, errors for data correction

Compensation is the right tool for reversing real-world side-effects (booking
cancellations, payment refunds, inventory holds). For invalid data — where you
just want to retry with corrected input — use an error boundary that loops back
to a user task instead.

### Escalation for SLA-breach handoff

If you want to keep working but inform someone, use a non-interrupting escalation
boundary on the host sub-process. The parallel handler can send a notification,
create a ticket, or escalate to a human, while the original work proceeds.

### Catch-all boundary as a safety net

A boundary event with no `errorRef` catches any error not already handled by a
specific-code boundary. Useful as a final layer of "anything we didn't anticipate
goes here" — typically routing to a generic failure handler that logs and pages.

## Limitations and known issues

- **Child-process errors do not propagate through `CallActivity` error boundaries.**
  See the *Known limitation* callout above and `tests/manual/11-error-boundary/test-plan.md`.
- **Compensation of compensation is rejected at parse time.** A compensation
  handler activity may not itself have a `<compensateEventDefinition/>` boundary.
- **At most one compensation boundary** is allowed per compensable activity;
  duplicates fail the BPMN parse.
- The Hazard outcome of a Transaction Sub-Process (an unhandled error escaping
  the transaction scope) is tracked separately under the Cancel-events feature
  and is out of scope for this guide. See the
  [Transaction Sub-Process status](/fleans/concepts/bpmn-support/#transaction-sub-process-status)
  callout for current Hazard-path status and workaround.

## Related guides

- [BPMN Support](/fleans/concepts/bpmn-support/) — full element coverage table,
  including parser-level rules for each event family discussed here.
- [Service Tasks](/fleans/guides/service-tasks/) — how external workers complete
  service-task activities, and how to surface failures back to the engine.
- [Writing Custom-Task Plugins](/fleans/guides/writing-custom-tasks/) — how plugin
  authors throw `CustomTaskFailedActivityException` with custom error codes.
