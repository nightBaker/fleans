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
| **Transaction Sub-Process** (`<bpmn:transaction>`) | You need atomic-or-compensate semantics — Cancel End Event triggers a Cancel Boundary on the transaction; compensation handlers run in reverse order. | Same as Embedded SubProcess for variables. | Like Embedded, but with a defined **outcome** (`Completed` / `Cancelled` / `Hazard`) recorded on the parent's transaction-outcome record. |

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

Fixture: [`embedded-subprocess.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/07-subprocess/embedded-subprocess.bpmn).

```xml
<bpmn:subProcess id="risk-check">
  <bpmn:startEvent id="sub-start" />
  <bpmn:scriptTask id="score" scriptFormat="csharp">
    <bpmn:script>_context.score = _context.amount * 0.01;</bpmn:script>
  </bpmn:scriptTask>
  <bpmn:endEvent id="sub-end" />
  <bpmn:sequenceFlow id="sub-f1" sourceRef="sub-start" targetRef="score" />
  <bpmn:sequenceFlow id="sub-f2" sourceRef="score" targetRef="sub-end" />
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

The child does not need a wait state. A child that goes straight from start to
end, or fails or throws an escalation before its first wait, ends the call
activity right away like any other child: it completes, follows an error
boundary or is caught by an escalation boundary.

<figure class="arch-diagram" style="--arch-ratio: 540 / 818; max-width: 580px; margin-inline: auto">
  <iframe data-arch-src="/fleans/diagrams/call-activity-sequence.html" src="/fleans/diagrams/call-activity-sequence.html?embed=1" title="Call activity parent/child sequence" loading="lazy"></iframe>
  <figcaption>A call activity starts a separate child instance on the latest version of <code>calledElement</code>, then waits for the child to complete or fail. <a href="/fleans/diagrams/call-activity-sequence.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

Fixture: [`parent-process.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/06-call-activity/parent-process.bpmn) (parent) and
[`child-process.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/06-call-activity/child-process.bpmn) (child).

### Variable mapping syntax — bare `<inputMapping>` / `<outputMapping>` only

CallActivity input/output mappings use **bare `<inputMapping>` and
`<outputMapping>` elements** inside the activity's `<extensionElements>`
block. The parser matches them by **local-name** — any namespace prefix is fine
as long as the local-name is `inputMapping` / `outputMapping`.

From the `parent-process.bpmn` fixture:

```xml
<bpmn:callActivity id="callChild" calledElement="child-process">
  <bpmn:extensionElements>
    <inputMapping source="input" target="input" />
    <outputMapping source="result" target="result" />
  </bpmn:extensionElements>
</bpmn:callActivity>
```

:::caution[`<fleans:ioMapping>` is a different mechanism]
`<fleans:ioMapping>` (`<fleans:input>` / `<fleans:output>`) on a call activity is the general
[input/output mapping](/fleans/concepts/activities/io-mapping/), not a replacement for
`<inputMapping>` / `<outputMapping>`. They work in layers:

1. `fleans:input`s compute **call-activity-local** variables from C# expressions.
2. The propagation flags and `<inputMapping>`s build the child's variables from the parent scope
   **including** those locals.
3. On completion, `propagateAllChildVariables` and `<outputMapping>`s build the call activity's
   result from the child's variables.
4. `fleans:output`s, when present, pick what of that result reaches the parent scope; the rest is
   dropped.

Copying `<fleans:input>` XML from a service task into a call activity therefore does **not** pass
variables to the child by name unless propagation is on (the default) or an `<inputMapping>`
forwards them.
:::

### Two propagation knobs

A call activity takes two boolean attributes that decide how the
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

Call activities always resolve to the **latest deployed version** of the
called process at the moment the call activity starts its child — the same
lookup every other start uses (`IProcessDefinitionGrain.GetStartableDefinition`),
which also refuses a disabled process.

Practical implications:

- **New deploys are picked up immediately.** Each `POST /Definitions/deploy`
  increments the version of `calledElement`. Parent instances starting
  *after* the deploy use the new version, and **in-flight parents that
  reach a call activity after the deploy also use the new version** — there
  is no pinning today.
- **Already-running child instances keep their version.** A child that
  started under version 3 stays on version 3 until it terminates, even if
  version 4 is deployed mid-flight.
- **A disabled called process fails the call activity.** A disabled process
  starts no new instances, a call activity's child included: the call
  activity fails with `Process '…' is disabled. Enable it before creating new
  instances.`, like any other child-start failure. An error boundary on the
  call activity catches it; without one, it is an
  [unhandled error](/fleans/guides/error-handling/#unhandled-errors-fail-the-instance).
- **No `<calledElement-version>` attribute.** Per-call version pinning is a
  future feature; if you need it today, the workaround is to use distinct
  `calledElement` keys for distinct versions (e.g. `payment-charge-v3`).

If you need a stable contract across deploys, treat the called process
boundary as a **contract** — keep input/output names stable and avoid
breaking changes to the variables crossing the mapping.

## Error propagation

When an activity in the child **fails** and nothing in the child catches it,
the child instance fails and the failure is re-raised on the parent's call
activity, where an error boundary catches it and the parent continues on the
error path (the call activity is interrupted; the parent instance does not
fail). An uncaught *error end event* in the child does the same, with its
error code. See
[How an activity fails](/fleans/guides/error-handling/#how-an-activity-fails)
for what fails an activity.

The fixtures catch the child's failure with a catch-all boundary (no
`errorRef`):

```xml
<bpmn:callActivity id="callFailing" calledElement="child-that-fails" />
<bpmn:boundaryEvent id="errorBoundary" attachedToRef="callFailing">
  <bpmn:errorEventDefinition />
</bpmn:boundaryEvent>
```

Fixture: [`error-on-call-activity.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/11-error-boundary/error-on-call-activity.bpmn)
(the parent), paired with [`child-that-fails.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/11-error-boundary/child-that-fails.bpmn),
whose script task throws. See [Error Handling](/fleans/guides/error-handling/) for the catch-all
vs specific-code matching rules.

## Cancelling a call activity

When a call activity is cancelled — an interrupting boundary fires on it, an event sub-process
interrupts its scope, or the enclosing sub-process is cancelled — the child instance is cancelled
too: its waiting activities, user tasks and subscriptions are released and it ends as
**Cancelled**. A child that finishes at the same moment is ignored by the parent, which stays on
the path it already took ([#847](https://github.com/nightBaker/fleans/issues/847)).

## Transaction sub-process

A `<bpmn:transaction>` is a sub-process with all-or-nothing semantics:

- A normal end event leaves the transaction with outcome **Completed** —
  variables merge into the parent scope as with any sub-process.
- A `<cancelEventDefinition>` end event leaves the transaction with outcome
  **Cancelled** — active sibling activities are cancelled, any compensation
  handlers attached to completed activities inside the transaction run in
  reverse order, and then the cancel boundary event on the transaction fires.
- An unhandled error escaping the transaction leaves it with outcome
  **Hazard** — active siblings are cancelled, completed compensable activities
  are compensated, and then an error boundary on the transaction fires (or the
  transaction fails, propagating the error to the parent scope). For nested
  transactions the inner one ends in Hazard first; when it has no error
  boundary of its own, its failure makes the outer transaction end in Hazard
  too, so the outer error boundary fires
  ([#1122](https://github.com/nightBaker/fleans/issues/1122)).

Fixture: [`happy-path.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/26-transaction-subprocess/happy-path.bpmn).

For the cancel-driven flow plus compensation handler walk semantics, see
[Error Handling — Compensation](/fleans/guides/error-handling/) — the
variable-scope merge invariant for handler walks lives there.

All three outcomes (Completed / Cancelled / Hazard) are supported; see the
[Transaction Sub-Process status](/fleans/concepts/bpmn-support/#transaction-sub-process-status)
callout on the BPMN coverage page for the details.

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
  <bpmn:boundaryEvent id="compFlight" attachedToRef="reserveFlight" cancelActivity="false">
    <bpmn:compensateEventDefinition />
  </bpmn:boundaryEvent>
  <!-- handler tasks, associations, sequence flows… -->
</bpmn:transaction>
```

A compensation boundary must carry `cancelActivity="false"` — without it the
boundary is interrupting by default and the deploy is rejected. Full mechanics — including the variable-scope merge invariant that keeps
handler side-effects visible to subsequent handlers — are in
[Error Handling — Compensation](/fleans/guides/error-handling/).

## Limitations and known issues

- **No `<calledElement-version>` pinning.** Call activities always resolve
  to the latest version of `calledElement`.
  In-flight parents pick up new versions on their next call-activity
  execution. Use distinct `calledElement` keys per version if you need
  pinning today.

## See also

- [Variables and Scope](/fleans/guides/variables-and-scope/) — how
  scopes nest, when child variables merge back, and the read-up /
  write-local rule.
- [Error Handling](/fleans/guides/error-handling/) — full mechanics for
  errors, escalations, and compensation.
- [BPMN Support](/fleans/concepts/bpmn-support/) — the canonical reference
  for which elements parse cleanly today.
