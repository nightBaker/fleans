---
title: Call Activities and Sub-Processes
description: When to use embedded sub-processes vs call activities vs transactions in Fleans — variable mapping syntax, versioning, error propagation, and worked examples.
---


Real workflows are rarely flat. You'll want to group related steps into a
**sub-process**, factor out a reusable workflow into a separately-deployed
**called process**, or wrap a multi-step block in transactional cancel /
compensation semantics.

This guide covers the three ways Fleans lets you compose workflow building
blocks — when to use each, how variables flow across the boundary, what
versioning does, and where the current implementation has gaps.

## When to use what

| Shape | Use when | Variable scope | Lifecycle |
| --- | --- | --- | --- |
| **Embedded SubProcess** (`<bpmn:subProcess>`) | The grouping is internal to one process — you just want a logical block, a shared boundary event, or a nested variable scope. | Child scope inherits reads from parent; writes are local; explicit merge on completion. See [Variables and Scope](/fleans/guides/variables-and-scope/). | Lives and dies with its parent instance — same workflow instance, same event stream. |
| **Call Activity** (`<bpmn:callActivity calledElement="…">`) | The called process is reusable across workflows — multiple parents call it, or it's deployed independently. | Two propagation knobs (`propagateAllParentVariables`, `propagateAllChildVariables`) plus explicit `<inputMapping>` / `<outputMapping>`. | Spawns a **separate workflow instance** with its own id and event stream. Parent waits for child completion. |
| **Transaction Sub-Process** (`<bpmn:transaction>`) | You need atomic-or-compensate semantics — Cancel End Event triggers a Cancel Boundary on the transaction; compensation handlers run in reverse order. | Same as Embedded SubProcess for variables. | Like Embedded, but with a defined **outcome** (`Completed` / `Cancelled`) recorded on the parent's transaction-outcome record. |

Quick rule of thumb:

- **One workflow** that's just visually busy → Embedded SubProcess.
- **Two workflows** with different lifecycles → Call Activity.
- **All-or-nothing** business unit → Transaction.

## Embedded SubProcess

A `<bpmn:subProcess>` runs inside its parent's instance — same workflow id,
same event stream, same overall lifecycle. Use it to:

- Group steps under a shared boundary event (e.g. a single timer that
  cancels the whole block).
- Open a nested variable scope so writes inside don't leak to siblings.
- Compose multi-step compensation (paired with a transaction).

Fixture: `tests/manual/07-subprocess/embedded-subprocess.bpmn` —
test plan in `tests/manual/07-subprocess/test-plan.md`.

```xml
<bpmn:subProcess id="risk-check">
  <bpmn:startEvent id="sub-start" />
  <bpmn:scriptTask id="score" scriptFormat="csharp">
    <bpmn:script>_context.score = _context.amount * 0.01;</bpmn:script>
  </bpmn:scriptTask>
  <bpmn:endEvent id="sub-end" />
  <bpmn:sequenceFlow sourceRef="sub-start" targetRef="score" />
  <bpmn:sequenceFlow sourceRef="score" targetRef="sub-end" />
</bpmn:subProcess>
```

Variables written inside the sub-process land on the **child scope** — not
on the root scope of the parent instance. They flow back to the parent
when the sub-process completes via the engine's merge event. The full
read-up / write-local / merge-on-completion model is documented in
[Variables and Scope](/fleans/guides/variables-and-scope/#scope-creation).

## Call Activity

A `<bpmn:callActivity calledElement="…">` runs a **different workflow
definition** as a separate instance. The parent's call-activity step waits
until the child instance terminates; on success, output mappings copy
variables back into the parent.

Fixture: `tests/manual/06-call-activity/parent-process.bpmn` (parent) and
`tests/manual/06-call-activity/child-process.bpmn` (child) — test plan in
`tests/manual/06-call-activity/test-plan.md`.

### Variable mapping syntax — bare `<inputMapping>` / `<outputMapping>` only

CallActivity input/output mappings use **bare `<inputMapping>` and
`<outputMapping>` elements** inside the activity's `<extensionElements>`
block. The parser at [BpmnConverter.cs#L616](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs#L616) (inputMapping) and `:623`
(outputMapping) matches by **local-name** — any namespace prefix is fine
as long as the local-name is `inputMapping` / `outputMapping`.

Verbatim from `tests/manual/06-call-activity/parent-process.bpmn`:

```xml
<bpmn:callActivity id="callChild" calledElement="child-process">
  <bpmn:extensionElements>
    <inputMapping source="input" target="input" />
    <outputMapping source="result" target="result" />
  </bpmn:extensionElements>
</bpmn:callActivity>
```

Source-of-truth pin (the call-activity mapping match):

```csharp
// See: https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs#L616
foreach (var input in extensionElements.Elements()
    .Where(e => e.Name.LocalName == "inputMapping"))
// See: https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs#L623
foreach (var output in extensionElements.Elements()
    .Where(e => e.Name.LocalName == "outputMapping"))
```

:::caution[`<fleans:input>` / `<fleans:output>` are NOT accepted by CallActivity]
The `<fleans:input>` / `<fleans:output>` form is reserved for **service tasks
and custom tasks** (and the equivalent `<zeebe:input>` / `<zeebe:output>` from
Camunda exports, accepted via the same parser). Its parser is at
[BpmnConverter.cs#L1286-L1322](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs#L1286-L1322) and is never invoked for `<callActivity>`.

Mixing the two forms — for example writing
`<fleans:input source="…" target="…"/>` inside a
`<callActivity><extensionElements>` block — will silently produce **zero
mappings**: the call-activity parser only walks elements whose local-name
is `inputMapping` / `outputMapping`, and the engine emits no parse warning
for stray service-task-style elements there. The child workflow will start with
whatever the propagation flags pull in (and nothing else).

If you copy mapping XML from a service task into a call activity, you must
rename the elements.
:::

### Two propagation knobs

[CallActivity.cs#L8-L14](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Domain/Activities/CallActivity.cs#L8-L14) exposes two boolean attributes that decide how the
parent's variables seed the child and how the child's variables flow back:

```xml
<bpmn:callActivity id="callChild" calledElement="child"
    propagateAllParentVariables="true"
    propagateAllChildVariables="true">
  <bpmn:extensionElements>
    <inputMapping  source="amount"      target="amount" />
    <outputMapping source="approvalId"  target="approvalId" />
  </bpmn:extensionElements>
</bpmn:callActivity>
```

| Attribute | Default | Effect |
| --- | --- | --- |
| `propagateAllParentVariables` | `true` | Copy the **entire parent scope** into the child on start, then layer explicit `<inputMapping>` overrides on top. |
| `propagateAllChildVariables` | `true` | Copy the **entire child scope** back into the parent on completion, then layer explicit `<outputMapping>` overrides on top. |

Set both to `false` for **hard isolation** — only the explicit mappings cross
the boundary. This is the right default for genuinely-shared library
processes whose internal variable names you want to keep stable as parents
evolve.

```xml
<bpmn:callActivity id="callShared" calledElement="payment-charge"
    propagateAllParentVariables="false"
    propagateAllChildVariables="false">
  <bpmn:extensionElements>
    <inputMapping  source="orderTotal"   target="amount" />
    <outputMapping source="chargeResult" target="paymentResult" />
  </bpmn:extensionElements>
</bpmn:callActivity>
```

## Versioning

Call activities always resolve to the **latest active version** of the
called process. The single resolution point lives at
[WorkflowLifecycleEffectHandler.cs#L61](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Application/Effects/WorkflowLifecycleEffectHandler.cs#L61):

```csharp
var processGrain = context.GrainFactory.GetGrain<IProcessDefinitionGrain>(
    startChild.ProcessDefinitionKey);
var childDefinition = await processGrain.GetLatestDefinition();
```

Practical implications:

- **New deploys are picked up immediately.** Each `POST /Workflow/deploy`
  increments the version of `calledElement`. Parent instances starting
  *after* the deploy use the new version, and **in-flight parents that
  reach a call activity after the deploy also use the new version** — there
  is no pinning today.
- **Already-running child instances keep their version.** A child that
  started under version 3 stays on version 3 until it terminates, even if
  version 4 is deployed mid-flight.
- **No `<calledElement-version>` attribute.** Per-call version pinning is a
  future feature; if you need it today, the workaround is to use distinct
  `calledElement` keys for distinct versions (e.g. `payment-charge-v3`).

If you need a stable contract across deploys, treat the called process
boundary as a **contract** — keep input/output names stable and avoid
breaking changes to the variables crossing the mapping.

## Error propagation

Boundary error events on a call activity work as expected for errors
**thrown explicitly from the child** as a `<bpmn:errorEventDefinition>` —
typically via an *error end event* inside the child process. The full
mechanics live in [Error Handling](/fleans/guides/error-handling/).

```xml
<bpmn:callActivity id="callChild" calledElement="child-process" />
<bpmn:boundaryEvent id="onChildError" attachedToRef="callChild">
  <bpmn:errorEventDefinition errorRef="ChildError" />
</bpmn:boundaryEvent>
<bpmn:error id="ChildError" errorCode="VALIDATION" />
```

Fixture: `tests/manual/11-error-boundary/error-on-call-activity.bpmn` —
the parent — paired with `tests/manual/11-error-boundary/child-that-fails.bpmn`.
See [Error Handling — error end events](/fleans/guides/error-handling/) for the catch-all
vs specific-code matching rules and the cancellation semantics.

:::caution[Known limitation: child errors don't bubble to parent CallActivity boundary]
Per `tests/manual/11-error-boundary/test-plan.md`, **child-process errors do
not currently propagate to a parent `CallActivity`'s error boundary**. The
boundary stays armed but never fires; the call activity stays in `Running`
state indefinitely.

**Workaround:** catch the error inside the child scope using an *error event
sub-process* (or a boundary event on a sub-process inside the child), and
exit the child cleanly via a normal end event. The parent's call activity
will then complete normally, and you can branch in the parent on a variable
the child set to indicate which recovery path was taken.

This is regression item **#11 in the manual-test list** (`KNOWN BUG` until
resolved). The full discussion of error/escalation/compensation lives in
the [Error Handling guide](/fleans/guides/error-handling/).
:::

## Transaction sub-process

A `<bpmn:transaction>` is a sub-process with all-or-nothing semantics:

- A normal end event leaves the transaction with outcome **Completed** —
  variables merge into the parent scope as with any sub-process.
- A `<cancelEventDefinition>` end event leaves the transaction with outcome
  **Cancelled** — active sibling activities are cancelled, the cancel
  boundary event on the transaction fires, and any compensation handlers
  attached to completed activities inside the transaction run in reverse
  order.

Fixture: `tests/manual/26-transaction-subprocess/happy-path.bpmn` — test
plan in `tests/manual/26-transaction-subprocess/test-plan.md`.

For the cancel-driven flow plus compensation handler walk semantics, see
[Error Handling — Compensation](/fleans/guides/error-handling/) — the
variable-scope merge invariant for handler walks lives there.

For the full phase-1 status (Completed ✅ / Cancelled ✅ / Hazard ❌)
plus the recommended workaround for Hazard-style cleanup, see the
[Transaction Sub-Process status](/fleans/concepts/bpmn-support/#transaction-sub-process-status)
callout on the BPMN coverage page.

## Best-practice cookbook

### 1. Reusable approval workflow as a Call Activity

A loan-approval process is invoked by both the *new-loan* and *renewal*
workflows. Define it once and call it twice:

```xml
<!-- new-loan.bpmn -->
<bpmn:callActivity id="approve" calledElement="loan-approval"
    propagateAllParentVariables="false"
    propagateAllChildVariables="false">
  <bpmn:extensionElements>
    <inputMapping  source="loanAmount"      target="amount" />
    <inputMapping  source="customerId"      target="customerId" />
    <outputMapping source="approved"        target="loanApproved" />
    <outputMapping source="approvalReason"  target="loanApprovalReason" />
  </bpmn:extensionElements>
</bpmn:callActivity>
```

Hard isolation (`propagateAll*=false`) keeps the approval workflow's
internal variable names stable as parents evolve.

### 2. Visual grouping with a shared timer

When the *whole risk check* should be cancelled if it takes more than 30
seconds, wrap the steps in an **embedded sub-process** with a timer
boundary — one boundary instead of one per step:

```xml
<bpmn:subProcess id="risk-check">
  <!-- start, score, threshold check, end -->
</bpmn:subProcess>
<bpmn:boundaryEvent id="riskTimeout" attachedToRef="risk-check"
    cancelActivity="true">
  <bpmn:timerEventDefinition>
    <bpmn:timeDuration>PT30S</bpmn:timeDuration>
  </bpmn:timerEventDefinition>
</bpmn:boundaryEvent>
```

### 3. Atomic booking step with compensation

Wrap *reserve flight + reserve hotel + charge card* in a transaction.
Each reservation has a compensation handler. If anything fails or the
transaction throws cancel, the handlers run in reverse order:

```xml
<bpmn:transaction id="book-trip">
  <bpmn:serviceTask id="reserveFlight" />
  <bpmn:serviceTask id="reserveHotel" />
  <bpmn:serviceTask id="charge" />
  <bpmn:boundaryEvent id="compFlight" attachedToRef="reserveFlight">
    <bpmn:compensateEventDefinition />
  </bpmn:boundaryEvent>
  <!-- handler tasks, sequence flows… -->
</bpmn:transaction>
```

Full mechanics — including the variable-scope merge invariant that keeps
handler side-effects visible to subsequent handlers — are in
[Error Handling — Compensation](/fleans/guides/error-handling/).

## Limitations and known issues

- **#11 KNOWN BUG — child errors don't bubble to parent CallActivity
  boundary.** The boundary stays armed but never fires; the call activity
  stays `Running`. Use the error-event-sub-process workaround above. See
  `tests/manual/11-error-boundary/test-plan.md`.
- **No `<calledElement-version>` pinning.** Call activities always resolve
  to the latest version of `calledElement`
  ([WorkflowLifecycleEffectHandler.cs#L61](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Application/Effects/WorkflowLifecycleEffectHandler.cs#L61) — `GetLatestDefinition()`).
  In-flight parents pick up new versions on their next call-activity
  execution. Use distinct `calledElement` keys per version if you need
  pinning today.
- **Transaction Hazard path** — see the
  [Transaction Sub-Process status](/fleans/concepts/bpmn-support/#transaction-sub-process-status)
  callout on the BPMN coverage page for the supported/unsupported
  outcome matrix and the workaround pattern.

## See also

- [Variables and Scope](/fleans/guides/variables-and-scope/) — how
  scopes nest, when child variables merge back, and the read-up /
  write-local rule.
- [Error Handling](/fleans/guides/error-handling/) — full mechanics for
  errors, escalations, and compensation.
- [BPMN Support](/fleans/concepts/bpmn-support/) — the canonical reference
  for which elements parse cleanly today.
