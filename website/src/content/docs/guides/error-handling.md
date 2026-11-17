---
title: Error Handling
description: How to model structured failure recovery in Fleans — BPMN error boundary events, escalations, and compensation handlers, with worked examples.
---


Workflows fail. External calls time out, data is invalid, business rules are violated.
Fleans implements three orthogonal BPMN mechanisms for structured failure recovery —
**errors**, **escalations**, and **compensation** — each suited to a different shape of problem.

This guide is a developer's tour of all three, anchored to the runnable fixtures
in the repository's [`tests/manual/`](https://github.com/nightBaker/fleans/tree/main/tests/manual) folder. For the underlying parsing rules, the canonical reference is
the [BPMN Support](/fleans/concepts/bpmn-support/) page.

## When to use what

| Situation | Mechanism | Catches | Side-effect semantics |
| --- | --- | --- | --- |
| Recoverable failure inside a task (validation, timeout, 5xx response) | **Error** | Error boundary on activity / error event sub-process | Activity ends in `Failed`; execution routes through the boundary's outgoing flow |
| Out-of-band domain signal (SLA breach, manager-needs-to-know, "still continuing") | **Escalation** | Escalation boundary on `SubProcess` / `CallActivity` | Interrupting boundary cancels the host activity; non-interrupting runs the handler in parallel and the host continues |
| Reverse a successfully-completed activity (cancel a reservation that was made) | **Compensation** | Compensation boundary on the original activity | Reverse-order handler walk; each handler runs in an isolated child scope seeded with the compensable activity's completion-time variables |

The decision matrix:

- The activity *failed* and you want recovery → **Error**.
- The activity *succeeded* but a downstream step needs to undo it → **Compensation**.
- The activity is *still running* (or completed normally) but a sibling needs to know
  something out-of-the-ordinary happened → **Escalation**.

## Error events

Use error events whenever an activity can fail in a way the workflow can recover from.
Fleans supports two ways to catch an error:

<figure class="arch-diagram" style="--arch-ratio: 720 / 546">
  <iframe data-arch-src="/fleans/diagrams/error-boundary-flow.html" src="/fleans/diagrams/error-boundary-flow.html?embed=1" title="Error boundary matching and compensation flow" loading="lazy"></iframe>
  <figcaption>A failed activity's error code is matched against its boundaries: a specific code wins over a catch-all; a compensate throw later undoes completed compensable activities in reverse order. <a href="/fleans/diagrams/error-boundary-flow.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

1. **Error boundary event** attached to a `serviceTask` / `scriptTask` / `userTask` / `callActivity`.
2. **Error event sub-process** (`<subProcess triggeredByEvent="true">` containing
   `<startEvent><errorEventDefinition/></startEvent>`).

:::caution[Error end events do not throw]
An error end event (`<endEvent><errorEventDefinition/></endEvent>`) is parsed as a plain end
event: the scope ends normally and nothing catches anything
([#843](https://github.com/nightBaker/fleans/issues/843)). An error reaches a boundary or an event sub-process only when an
activity **fails** — see [How an activity fails](#how-an-activity-fails).
:::

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

Per BPMN spec, error boundaries are **always interrupting** — Fleans ignores
`cancelActivity="false"` on an error boundary and treats it as interrupting (the deploy
does not fail), and the management UI's editor disables the *Interrupting* checkbox for them.

Full fixture: [`error-on-call-activity.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/11-error-boundary/error-on-call-activity.bpmn).

The same boundary works on a `callActivity`: an unhandled error in the child
process is propagated to the parent call activity, and its error boundary routes
the parent onto the error path.

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

Full fixture: [`error-event-subprocess.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/19-event-subprocess-error/error-event-subprocess.bpmn).

## How an activity fails

An error boundary or error event sub-process fires only when an activity **fails**. Today an
activity fails in one of these ways:

| Source | How | Error code |
| --- | --- | --- |
| Script task | Any exception while the script runs (or its 10 s timeout) | always `500` |
| Custom task on a gRPC worker | Handler throws `ExternalTaskFailedException(code, message)` | the handler's `code` |
| Custom task on a gRPC worker | Handler throws any other exception | `500` |
| Any waiting task completed over REST | `POST /Execution/fail-activity` with `ErrorCode` | the request's `ErrorCode` |
| User task | `POST /UserTasks/{activityInstanceId}/fail` with `ErrorCode` | the request's `ErrorCode` (default `500`) |
| Call activity | The child instance fails with an unhandled error | see [Call Activities](/fleans/guides/call-activities-and-subprocesses/#error-propagation) |

See the [REST reference](/fleans/reference/api/) for the request bodies, and
[User Tasks](/fleans/guides/user-tasks/) for the user-task endpoints.

### From a script task

A script cannot throw a typed error: it has no statements (`if`, `throw`) and no exception types
to construct — see [what a script can contain](/fleans/concepts/activities/tasks/#what-a-script-can-contain).
Any exception it raises fails the activity with code **`500`**, so catch it with an error
boundary whose `<bpmn:error>` has `errorCode="500"`, or with a catch-all boundary (no `errorRef`).

To fail on a business rule, the engine's own test fixtures use a ternary around a call that
throws:

```xml
<bpmn:scriptTask id="validate" scriptFormat="csharp">
  <bpmn:script>_context.check = _context.amount > 10000 ? int.Parse("amount exceeds limit") : 0; _context.validated = true</bpmn:script>
</bpmn:scriptTask>
```

When `amount` is over the limit, `int.Parse` throws and the activity fails with code `500`; the
error message is the parse exception's message. If you need a specific code, route with an
[exclusive gateway](/fleans/guides/gateways-and-conditions/) instead, or do the check in a custom
task that throws `ExternalTaskFailedException`.

### From a custom-task handler — `ExternalTaskFailedException`

Handlers throw this when their integration fails (HTTP 5xx, third-party
rejection, etc.) and supply their own error code:

```csharp
public override async Task<IDictionary<string, object?>> ExecuteAsync(
    IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
    ExternalTaskContext context, CancellationToken ct)
{
    var response = await _http.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
        throw new ExternalTaskFailedException(
            code: ((int)response.StatusCode).ToString(),
            message: $"Upstream returned {response.StatusCode}");
    // …
}
```

The error code is whatever the handler passes, so error boundaries can match on
specific HTTP statuses (`"404"`, `"504"`, etc.). The [REST Caller plugin guide](https://github.com/nightBaker/fleans-plugins/blob/main/docs/rest-caller/README.md)
(fleans-plugins) has an end-to-end worked example.

### Anything else — generic `Exception`

If a script or handler throws *any other* exception, the activity fails with code
**`"500"`** and the exception's `Message`. In a handler, use this only when you can't
classify the failure — prefer `ExternalTaskFailedException`.

### Unhandled errors fail the instance

If nothing catches the error — no matching error boundary event and no error
event sub-process in any enclosing scope — the activity is recorded as failed
(its `ErrorState` carries the code and message). When that failure leaves the
instance with **no active activities**, the instance itself terminates as
**Failed**:

- the instance-state snapshot reports `isFailed: true` (together with
  `isCompleted: true` and `completedAt`, because a failed instance is terminal —
  the same convention `isCancelled` uses), and the Web UI shows a red **Failed**
  badge;
- the engine logs EventId **1120** at `Error` level naming the failed activity
  and its error;
- `fleans.workflow.terminated` is incremented with `result="failed"`.

If other branches are still active (for example after a parallel fork), the
instance keeps running; it fails only once no active activities remain. A branch
that reaches a root end event first still completes the instance, as before.

Before this behaviour existed, such an instance silently stayed *Running* with
no active activities. If you need the workflow to continue instead, attach an
error boundary event (a catch-all one with no `errorRef` works) or add an error
event sub-process.

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

Full fixtures in [`tests/manual/24-escalation-event/`](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-escalation-event) —
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
<bpmn:boundaryEvent id="comp-hotel" attachedToRef="reserve-hotel" cancelActivity="false">
  <bpmn:compensateEventDefinition />
</bpmn:boundaryEvent>
<bpmn:scriptTask id="cancel-hotel" scriptFormat="csharp" isForCompensation="true"> … </bpmn:scriptTask>
<bpmn:association id="comp-hotel-assoc" sourceRef="comp-hotel" targetRef="cancel-hotel" />
```

Parsing rules Fleans enforces at deploy:

1. The compensation boundary must carry **`cancelActivity="false"`**. A boundary without the
   attribute is interrupting by default, and an interrupting compensation boundary is rejected.
2. The boundary needs an `<association>` from it (`sourceRef`) to its handler (`targetRef`).
3. **At most one** compensation boundary per compensable activity.
4. The handler activity must have **no incoming sequence flow** — handlers are
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
emits a `VariablesMerged` event with the handler's full variable map targeting
the parent scope's variables.
:::

Full fixture: [`compensation-broadcast.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/24-compensation-event/compensation-broadcast.bpmn) —
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

- **Compensation of compensation is rejected at parse time.** A compensation
  handler activity may not itself have a `<compensateEventDefinition/>` boundary.
- **At most one compensation boundary** is allowed per compensable activity;
  duplicates fail the BPMN parse.
- **Error end events do not throw** — they are parsed as plain end events
  ([#843](https://github.com/nightBaker/fleans/issues/843)). Fail an activity instead (see
  [How an activity fails](#how-an-activity-fails)).
- **Escalation-triggered event sub-processes are not supported** — the escalation
  start event is parsed as a plain start event ([#783](https://github.com/nightBaker/fleans/issues/783)). Use an
  escalation boundary on the sub-process or call activity.
- An unhandled error escaping a Transaction Sub-Process gives the **Hazard**
  outcome: active siblings are cancelled, completed compensable activities are
  compensated, then an error boundary on the transaction fires (or the
  transaction fails). See
  [Transaction Sub-Process status](/fleans/concepts/bpmn-support/#transaction-sub-process-status).

## Related guides

- [BPMN Support](/fleans/concepts/bpmn-support/) — full element coverage table,
  including parser-level rules for each event family discussed here.
- [Service Tasks](/fleans/guides/service-tasks/) — how external workers complete
  service-task activities, and how to surface failures back to the engine.
- [Writing custom tasks](/fleans/guides/writing-custom-tasks/) — how handlers
  throw `ExternalTaskFailedException` with custom error codes.
