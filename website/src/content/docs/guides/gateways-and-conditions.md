---
title: Gateways, Timers and Signals
description: How to branch and join with exclusive, parallel and inclusive gateways, write sequence-flow conditions and default flows, and use timer and signal events in Fleans.
---

This guide covers the elements you use to steer a token: **gateways** with conditions and default
flows, **timers**, and **signals**. Every rule here is what the Fleans BPMN parser and engine do
today; the [BPMN Support](/fleans/concepts/bpmn-support/) matrix lists every element and its status.

All examples are fragments of a `<process>` inside a `<definitions>` element that declares the BPMN
namespace as the default (`xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"`). Every element,
including every `<sequenceFlow>`, needs an `id`.

## Branch only at gateways

A task or event follows **only its first outgoing sequence flow**: drawing two flows out of a task
does not split the token, and a `<conditionExpression>` on a flow that leaves a task is ignored
([#856](https://github.com/nightBaker/fleans/issues/856)). Put a gateway after the task whenever
the path forks or depends on data.

## Exclusive gateway — pick one path

An `<exclusiveGateway>` evaluates the conditions on its outgoing flows and takes **one** flow whose
condition is `true`. If none is true, it takes the flow named in its `default` attribute.

```xml
<exclusiveGateway id="checkAmount" default="toAuto" />

<sequenceFlow id="toReview" sourceRef="checkAmount" targetRef="manualReview">
  <conditionExpression>amount &gt; 1000</conditionExpression>
</sequenceFlow>
<sequenceFlow id="toAuto" sourceRef="checkAmount" targetRef="autoApprove" />
```

Rules:

- `default="…"` holds the **id of an outgoing sequence flow**, not of the target element. The
  default flow has no condition.
- When a gateway has conditional flows, an outgoing flow that has neither a condition nor the
  `default` attribute is **never taken**. Always mark the fallback flow as the default.
- If no condition is `true` and there is no default flow, the gateway fails like any failed
  activity — an [error boundary or error event sub-process](/fleans/guides/error-handling/) can
  catch it; otherwise the instance fails.
- Make the conditions mutually exclusive. When more than one is `true`, only one flow is taken and
  you should not rely on which.
- An exclusive gateway with **no** conditional flows merges: it passes the token on along its
  unconditional outgoing flow. Use it to join alternative paths again.

## Parallel gateway — all paths, then wait for all

A `<parallelGateway>` with more outgoing than incoming flows is a **fork**: it starts every
outgoing flow at once (conditions on them are ignored). One with more incoming than outgoing flows
is a **join**: it waits until a token has arrived on every incoming flow.

```xml
<parallelGateway id="fork" />
<parallelGateway id="join" />

<sequenceFlow id="toFork"   sourceRef="receiveOrder" targetRef="fork" />
<sequenceFlow id="toPack"   sourceRef="fork" targetRef="pack" />
<sequenceFlow id="toBill"   sourceRef="fork" targetRef="bill" />
<sequenceFlow id="packDone" sourceRef="pack" targetRef="join" />
<sequenceFlow id="billDone" sourceRef="bill" targetRef="join" />
<sequenceFlow id="toShip"   sourceRef="join" targetRef="ship" />
```

- A gateway with several incoming **and** several outgoing flows is rejected at deploy — split it
  into a join followed by a fork.
- Each branch works on its own copy of the variables; the copies are merged at the join, last write
  wins. See [Variables and Scope](/fleans/guides/variables-and-scope/#parallel-fork-join--merge-at-join-not-as-you-go).

## Inclusive gateway — every path whose condition holds

An `<inclusiveGateway>` fork evaluates **all** conditions on its outgoing flows and starts every flow
whose condition is `true`. If none is true, it takes the `default` flow; with no default, the
gateway fails. The matching inclusive join waits for the branches that were actually started.

```xml
<inclusiveGateway id="notify" default="toLog" />
<inclusiveGateway id="notifyDone" />

<sequenceFlow id="toEmail" sourceRef="notify" targetRef="sendEmail">
  <conditionExpression>${wantsEmail == true}</conditionExpression>
</sequenceFlow>
<sequenceFlow id="toSms" sourceRef="notify" targetRef="sendSms">
  <conditionExpression>${wantsSms == true}</conditionExpression>
</sequenceFlow>
<sequenceFlow id="toLog" sourceRef="notify" targetRef="logOnly" />

<sequenceFlow id="emailDone" sourceRef="sendEmail" targetRef="notifyDone" />
<sequenceFlow id="smsDone"   sourceRef="sendSms"   targetRef="notifyDone" />
<sequenceFlow id="logDone"   sourceRef="logOnly"   targetRef="notifyDone" />
```

As with the exclusive gateway, an outgoing flow with no condition that is not the default is never
taken, and a gateway with several incoming and several outgoing flows is rejected.

## Writing conditions

A `<conditionExpression>` is a C# boolean expression, evaluated with
[DynamicExpresso](https://github.com/dynamicexpresso/DynamicExpresso) against the variables of the
token's scope:

- Reference variables by **bare name** (`amount > 1000`) or with **`${…}`** (`${amount > 1000}`). At
  deploy the engine rewrites both to `_context.amount`; an explicit `_context.amount` is left as is.
  Names inside string literals are not rewritten: `status == "approved"`.
- Use comparisons, `&&`, `||`, `!` and arithmetic. Escape `<`, `>` and `&` in XML (`&lt;`, `&gt;`,
  `&amp;&amp;`), or wrap the expression in `<![CDATA[ … ]]>`.
- Reference **top-level variables only**. The rewrite also prefixes the part after a dot, so
  `order.total` or `name.Length` does not work; copy the value into a top-level variable with a
  script task first (`_context.orderTotal = _context.order.total`).
- Don't start the expression with `=` (the Zeebe/FEEL marker): it is not stripped, and the condition
  no longer compiles.
- Every condition is compiled at deploy; one that does not compile rejects the deploy and names the
  sequence flow.

The same syntax applies to [conditional events](/fleans/concepts/bpmn-support/#conditional-events)
and to a complex gateway's `activationCondition`. A multi-instance `completionCondition` is
different: it sees only the `nrOf*` counters and needs explicit `_context.` — see
[Multi-Instance Activities](/fleans/guides/multi-instance-activities/#completion-condition).

## Timers

A `<timerEventDefinition>` can sit on a start event, an intermediate catch event, a boundary event
(interrupting or with `cancelActivity="false"`) and the start event of an event sub-process. It holds
exactly one of three child elements:

| Element | Format | Example | Meaning |
| --- | --- | --- | --- |
| `<timeDuration>` | ISO 8601 duration | `PT30S`, `PT4H`, `P1D` | Fires once, this long after the timer is armed. |
| `<timeDate>` | ISO 8601 date-time | `2026-12-01T09:00:00Z` | Fires once at that instant; a date in the past fires immediately. |
| `<timeCycle>` | `R<n>/<duration>` or `R/<duration>` | `R3/PT10M`, `R/PT1H` | Fires every `<duration>`, `n` times, or forever with `R/`. |

```xml
<!-- Wait 30 seconds -->
<intermediateCatchEvent id="wait">
  <timerEventDefinition>
    <timeDuration>PT30S</timeDuration>
  </timerEventDefinition>
</intermediateCatchEvent>

<!-- Remind every hour, three times, while the review is still open -->
<boundaryEvent id="remind" attachedToRef="review" cancelActivity="false">
  <timerEventDefinition>
    <timeCycle>R3/PT1H</timeCycle>
  </timerEventDefinition>
</boundaryEvent>

<!-- Start a new instance every day -->
<startEvent id="daily">
  <timerEventDefinition>
    <timeCycle>R/P1D</timeCycle>
  </timerEventDefinition>
</startEvent>
```

Rules:

- Expressions are **literal values**: `${…}` or variable references are not evaluated.
- A cycle has exactly two parts. The ISO forms with a start or end date
  (`R3/2026-12-01T09:00:00Z/PT1H`) are not accepted.
- A cycle **repeats** on a timer start event, a non-interrupting boundary event and a
  non-interrupting event sub-process. On an intermediate catch event or an interrupting boundary,
  only its first interval counts — the timer fires once.
- The expression is not checked when the BPMN is parsed; an invalid one fails when the timer is
  scheduled.

## Signals

A signal is a **broadcast**: every instance waiting on it is triggered at once, and no correlation
key is involved (for one-to-one delivery use a [message](/fleans/guides/message-correlation/)).

Declare the signal once, as a child of `<definitions>`, and reference it by id with `signalRef`:

```xml
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="signals-example"
             targetNamespace="http://bpmn.io/schema/bpmn">
  <signal id="sig_alert" name="globalAlert" />

  <process id="alert-listener" isExecutable="true">
    <startEvent id="start" />
    <intermediateCatchEvent id="waitAlert">
      <signalEventDefinition signalRef="sig_alert" />
    </intermediateCatchEvent>
    <scriptTask id="handle" scriptFormat="csharp">
      <script>_context.signalReceived = true</script>
    </scriptTask>
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="waitAlert" />
    <sequenceFlow id="f2" sourceRef="waitAlert" targetRef="handle" />
    <sequenceFlow id="f3" sourceRef="handle" targetRef="end" />
  </process>
</definitions>
```

- **Catch** a signal with an intermediate catch event (above), a boundary event (interrupting or
  `cancelActivity="false"`), a signal start event (`<startEvent>` with a `<signalEventDefinition>`,
  which starts a new instance), or the start event of an event sub-process.
- **Throw** a signal from a process with an intermediate throw event:

  ```xml
  <intermediateThrowEvent id="raiseAlert">
    <signalEventDefinition signalRef="sig_alert" />
  </intermediateThrowEvent>
  ```

  A signal *end* event does not broadcast — it is parsed as a plain end event
  ([#843](https://github.com/nightBaker/fleans/issues/843)); put an intermediate throw event before
  a plain end event instead.
- **Send** a signal from outside with `POST /Execution/signal`. With the
  [Quick Start](/fleans/guides/quick-start/) Compose bundle:

  ```bash
  curl -X POST http://localhost:8081/Execution/signal \
    -H "Content-Type: application/json" \
    -d '{"SignalName":"globalAlert"}'
  # → {"deliveredCount":1,"workflowInstanceIds":[...]}
  ```

  `SignalName` is the signal's `name`, not its `id`. The response is `404` when nothing is waiting
  on the signal and no process starts on it. See the [REST reference](/fleans/reference/api/).

## See also

- [BPMN Support](/fleans/concepts/bpmn-support/) — status of every element, including the
  complex and event-based gateways.
- [Error Handling](/fleans/guides/error-handling/) — boundaries and event sub-processes that catch
  failures.
- [Message Correlation](/fleans/guides/message-correlation/) — one-to-one events keyed by a
  correlation value.
