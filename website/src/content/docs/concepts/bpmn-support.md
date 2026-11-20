---
title: BPMN Support
description: Canonical coverage matrix for BPMN 2.0 elements in Fleans.
---


This page is the canonical coverage matrix for BPMN 2.0 elements in Fleans. Every row names the `BpmnConverter` method that parses the element and links the manual test fixture on GitHub that exercises it end-to-end ([`tests/manual/`](https://github.com/nightBaker/fleans/tree/main/tests/manual)).

## Status legend

| Emoji | Meaning |
|---|---|
| ✅ | Fully supported — parse handler + Activity class + tested end-to-end. |
| ⚠️ | Partial support — engine works but with caveats: (a) sub-features pending in a follow-up issue, OR (b) BPMN editor's properties panel may not expose this variant. Status reflects engine support; editor UI is a separate concern. |
| 🚧 | In progress / planned, tracking issue open. |
| ❌ | Not implemented. |

## Coverage matrix

### Start Events

| Element | BPMN XML | Status | Parser | Tested by | Notes |
|---|---|---|---|---|---|
| Plain Start Event | `<bpmn:startEvent>` | ✅ | `ParseActivities` | [#01](https://github.com/nightBaker/fleans/tree/main/tests/manual/01-basic-workflow/) | Default start of any process. |
| Timer Start Event | `<bpmn:startEvent><timerEventDefinition>` | ✅ | `ParseActivities` | [#08](https://github.com/nightBaker/fleans/tree/main/tests/manual/08-timer-events/) (E2E `TimerStartEventTests`) | Schedules a process instance via `RegisterOrUpdateReminder`. |
| Message Start Event | `<bpmn:startEvent><messageEventDefinition>` | ✅ | `ParseActivities` | [#16](https://github.com/nightBaker/fleans/tree/main/tests/manual/16-message-start-event/) | Auto-creates an instance on matching message delivery. |
| Signal Start Event | `<bpmn:startEvent><signalEventDefinition>` | ✅ | `ParseActivities` | [#17](https://github.com/nightBaker/fleans/tree/main/tests/manual/17-signal-start-event/) | Broadcast match creates an instance. |
| Error Start Event *(Event Sub-Process only)* | `<bpmn:startEvent><errorEventDefinition>` | ✅ | `ParseActivities` | [#19](https://github.com/nightBaker/fleans/tree/main/tests/manual/19-event-subprocess-error/) | Only valid inside `triggeredByEvent="true"` sub-processes. |
| Conditional Start Event | `<bpmn:startEvent><conditionalEventDefinition>` | ✅ | `ParseActivities` | [#24-conditional](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-conditional-event/) | Triggered via `POST /Execution/evaluate-conditions`. |
| Multiple Start Event | `<bpmn:startEvent>` with multiple event definitions | ✅ | `ParseActivities` | [#24-multiple](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-multiple-event/) (E2E `MultipleStartEventTests`) | First-fires-wins; surplus subscriptions cancelled. |

### Intermediate Catch Events

| Element | BPMN XML | Status | Parser | Tested by | Notes |
|---|---|---|---|---|---|
| Message Intermediate Catch | `<bpmn:intermediateCatchEvent><messageEventDefinition>` | ✅ | `ParseActivities` | [#09](https://github.com/nightBaker/fleans/tree/main/tests/manual/09-message-events/) | Correlates by `=variable` extension. |
| Signal Intermediate Catch | `<bpmn:intermediateCatchEvent><signalEventDefinition>` | ✅ | `ParseActivities` | [#10](https://github.com/nightBaker/fleans/tree/main/tests/manual/10-signal-events/) | Broadcast match unblocks. |
| Timer Intermediate Catch | `<bpmn:intermediateCatchEvent><timerEventDefinition>` | ✅ | `ParseActivities` | [#08](https://github.com/nightBaker/fleans/tree/main/tests/manual/08-timer-events/) | ISO 8601 duration / cycle / date. |
| Conditional Intermediate Catch | `<bpmn:intermediateCatchEvent><conditionalEventDefinition>` | ✅ | `ParseActivities` | [#24-conditional](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-conditional-event/) | Re-evaluated on every activity completion. |
| Multiple Intermediate Catch | `<bpmn:intermediateCatchEvent>` w/ multiple event-defs | ✅ | `ParseActivities` | [#24-multiple](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-multiple-event/) | First-fires-wins. |

### Intermediate Throw Events

| Element | BPMN XML | Status | Parser | Tested by | Notes |
|---|---|---|---|---|---|
| Signal Intermediate Throw | `<bpmn:intermediateThrowEvent><signalEventDefinition>` | ✅ | `ParseActivities` | [#10](https://github.com/nightBaker/fleans/tree/main/tests/manual/10-signal-events/) | Broadcasts cluster-wide. |
| Escalation Intermediate Throw | `<bpmn:intermediateThrowEvent><escalationEventDefinition>` | ✅ | `ParseActivities` | [#24-escalation](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-escalation-event/) | Mid-flow throw, continues execution after. |
| Compensation Intermediate Throw | `<bpmn:intermediateThrowEvent><compensateEventDefinition>` | ✅ | `ParseActivities` | [#24-compensation](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-compensation-event/) (E2E `CompensationEventTests`, `CompensationThrowAndEndEventTests`) | Broadcast or targeted (`activityRef`). |
| Multiple Intermediate Throw | `<bpmn:intermediateThrowEvent>` w/ multiple event-defs | ✅ | `ParseActivities` | [#24-multiple](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-multiple-event/) | Fires every defined signal. |

### End Events

| Element | BPMN XML | Status | Parser | Tested by | Notes |
|---|---|---|---|---|---|
| Plain End Event | `<bpmn:endEvent>` | ✅ | `ParseActivities` | [#01](https://github.com/nightBaker/fleans/tree/main/tests/manual/01-basic-workflow/) | Terminates the enclosing scope. |
| Error End Event | `<bpmn:endEvent><errorEventDefinition>` | ❌ | `ParseActivities` | — | Parsed as a plain end event: the scope ends normally and **no error is thrown**, so no error boundary or error event sub-process fires — [#843](https://github.com/nightBaker/fleans/issues/843). See [Error Handling](/fleans/guides/error-handling/#how-an-activity-fails) for the ways an activity can fail today. |
| Terminate End Event | `<bpmn:endEvent><terminateEventDefinition>` | ❌ | `ParseActivities` | — | Parsed as a plain end event: other active branches keep running — [#843](https://github.com/nightBaker/fleans/issues/843). |
| Message End Event | `<bpmn:endEvent><messageEventDefinition>` | ❌ | `ParseActivities` | — | Parsed as a plain end event: no message is sent — [#843](https://github.com/nightBaker/fleans/issues/843). |
| Signal End Event | `<bpmn:endEvent><signalEventDefinition>` | ❌ | `ParseActivities` | — | Parsed as a plain end event: no signal is broadcast — [#843](https://github.com/nightBaker/fleans/issues/843). Use a signal intermediate throw event before a plain end event instead. |
| Cancel End Event *(Transaction only)* | `<bpmn:endEvent><cancelEventDefinition>` | ✅ | `ParseActivities` | [#30-cancel](https://github.com/nightBaker/fleans/tree/main/tests/manual/30-cancel-event/) | Triggers transaction Cancel boundary. |
| Compensation End Event | `<bpmn:endEvent><compensateEventDefinition>` | ✅ | `ParseActivities` | [#24-compensation](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-compensation-event/) (E2E `CompensationThrowAndEndEventTests`) | Broadcasts compensation throw on terminate. |
| Escalation End Event | `<bpmn:endEvent><escalationEventDefinition>` | ✅ | `ParseActivities` | [#24-escalation](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-escalation-event/) | Throws escalation; uncaught escalation is non-faulting. |

### Boundary Events

| Element | BPMN XML | Status | Parser | Tested by | Notes |
|---|---|---|---|---|---|
| Error Boundary | `<bpmn:boundaryEvent><errorEventDefinition>` | ✅ | `ParseActivities` | [#11](https://github.com/nightBaker/fleans/tree/main/tests/manual/11-error-boundary/) (E2E `ErrorBoundaryOnTaskTests`) | Always interrupting per BPMN spec. Optional `errorRef` filters by code. |
| Timer Boundary | `<bpmn:boundaryEvent><timerEventDefinition>` | ✅ | `ParseActivities` | [#08](https://github.com/nightBaker/fleans/tree/main/tests/manual/08-timer-events/), [#15](https://github.com/nightBaker/fleans/tree/main/tests/manual/15-non-interrupting-boundaries/) | Interrupting + non-interrupting variants. Cycle timers re-register on non-interrupting. Attachable to tasks, sub-processes and intermediate catch events (an interrupted catch releases its own message/signal/timer subscription). |
| Message Boundary | `<bpmn:boundaryEvent><messageEventDefinition>` | ✅ | `ParseActivities` | [#09](https://github.com/nightBaker/fleans/tree/main/tests/manual/09-message-events/) | Interrupting + non-interrupting variants, incl. boundaries on an `IntermediateCatchEvent` host. |
| Signal Boundary | `<bpmn:boundaryEvent><signalEventDefinition>` | ✅ | `ParseActivities` | [#10](https://github.com/nightBaker/fleans/tree/main/tests/manual/10-signal-events/) | Interrupting + non-interrupting variants, incl. boundaries on an `IntermediateCatchEvent` host. |
| Escalation Boundary | `<bpmn:boundaryEvent><escalationEventDefinition>` | ✅ | `ParseActivities` | [#24-escalation](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-escalation-event/) | Interrupting + non-interrupting variants. Specific code matches take priority over catch-all. |
| Compensation Boundary | `<bpmn:boundaryEvent><compensateEventDefinition>` | ✅ | `ParseActivities` | [#24-compensation](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-compensation-event/) | Attached to compensable activity. |
| Conditional Boundary | `<bpmn:boundaryEvent><conditionalEventDefinition>` | ⚠️ | `ParseActivities` | [#24-conditional](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-conditional-event/) (E2E `ConditionalBoundaryEventTests`) | Interrupting + non-interrupting; non-interrupting is edge-triggered. The watcher only reads the host's own variable scope, so variable updates from parallel branches or boundary payloads do not trigger it yet — [#784](https://github.com/nightBaker/fleans/issues/784). |
| Cancel Boundary *(Transaction only)* | `<bpmn:boundaryEvent><cancelEventDefinition>` | ✅ | `ParseActivities` | [#30-cancel](https://github.com/nightBaker/fleans/tree/main/tests/manual/30-cancel-event/) | Always interrupting. Fires when Cancel End Event executes. |
| Multiple Boundary | `<bpmn:boundaryEvent>` w/ multiple event-defs | ✅ | `ParseActivities` | [#24-multiple](https://github.com/nightBaker/fleans/tree/main/tests/manual/24-multiple-event/) | First-fires-wins cancels host activity. |

### Tasks ([details](/fleans/concepts/activities/tasks/))

| Element | BPMN XML | Status | Parser | Tested by | Notes |
|---|---|---|---|---|---|
| Plain Task | `<bpmn:task>` | ✅ | `ParseActivities` | [#01](https://github.com/nightBaker/fleans/tree/main/tests/manual/01-basic-workflow/) | Waits: the token stays on the task until `POST /Execution/complete-activity` completes it (a `<bpmn:serviceTask>` without a type behaves the same). For a no-op step use a script task. |
| Script Task | `<bpmn:scriptTask scriptFormat="csharp">` | ✅ | `ParseActivities` | [#02](https://github.com/nightBaker/fleans/tree/main/tests/manual/02-script-tasks/) | C# expressions and assignments on `_context`, read from the `<bpmn:script>` child — see [what a script can contain](/fleans/concepts/activities/tasks/#what-a-script-can-contain). `scriptFormat="javascript"` runs full ECMAScript instead — see [JavaScript scripts](/fleans/concepts/activities/tasks/#javascript-scripts); `scriptFormat="python"` runs Python 3 — see [Python scripts](/fleans/concepts/activities/tasks/#python-scripts). |
| Service Task / Custom Task | `<bpmn:serviceTask type="…">` | ✅ | `ParseActivities` | [#37](https://github.com/nightBaker/fleans/tree/main/tests/manual/37-custom-task-framework/) | Plugin-based execution. |
| User Task | `<bpmn:userTask>` | ✅ | `ParseActivities` | [#18-user-task](https://github.com/nightBaker/fleans/tree/main/tests/manual/18-user-task/) | Claim/unclaim/complete lifecycle via `/UserTasks/*` — see [User Tasks](/fleans/guides/user-tasks/). |
| Call Activity | `<bpmn:callActivity calledElement="…">` | ✅ | `ParseActivities` | [#06](https://github.com/nightBaker/fleans/tree/main/tests/manual/06-call-activity/) | Cross-process variable mapping. |
| Send / Receive / Manual / Business Rule Task | `<bpmn:sendTask>`, `<bpmn:receiveTask>`, `<bpmn:manualTask>`, `<bpmn:businessRuleTask>` | ❌ | — | — | Not parsed. The element is skipped and sequence flows to or from it are dropped, so the process **deploys with a gap** in the flow. |

### Sub-Processes

| Element | BPMN XML | Status | Parser | Tested by | Notes |
|---|---|---|---|---|---|
| Embedded Sub-Process | `<bpmn:subProcess>` | ✅ | `ParseActivities` | [#07](https://github.com/nightBaker/fleans/tree/main/tests/manual/07-subprocess/) | Own start/end, isolated variable scope. |
| Event Sub-Process | `<bpmn:subProcess triggeredByEvent="true">` | ✅ | `ParseActivities` | [#19](https://github.com/nightBaker/fleans/tree/main/tests/manual/19-event-subprocess-error/), [#20-tm](https://github.com/nightBaker/fleans/tree/main/tests/manual/20-event-subprocess-timer/), [#21](https://github.com/nightBaker/fleans/tree/main/tests/manual/21-event-subprocess-message/), [#22](https://github.com/nightBaker/fleans/tree/main/tests/manual/22-event-subprocess-signal/), [#23](https://github.com/nightBaker/fleans/tree/main/tests/manual/23-event-subprocess-non-interrupting/) | Error-/timer-/message-/signal-triggered; interrupting + non-interrupting. An escalation start event is not supported — it is parsed as a plain start event, so the sub-process never fires ([#783](https://github.com/nightBaker/fleans/issues/783)). |
| Transaction Sub-Process | `<bpmn:transaction>` | ✅ | `ParseActivities` | [#26](https://github.com/nightBaker/fleans/tree/main/tests/manual/26-transaction-subprocess/), [#30-cancel](https://github.com/nightBaker/fleans/tree/main/tests/manual/30-cancel-event/), [#53-nested](https://github.com/nightBaker/fleans/tree/main/tests/manual/53-nested-transaction/) | Completed ✅, Cancelled ✅, Hazard ✅. All three terminal outcomes supported. See [Transaction Sub-Process status](#transaction-sub-process-status). |
| Multi-Instance (any host) | `<bpmn:*><multiInstanceLoopCharacteristics>` | ⚠️ | `TryWrapMultiInstance` | [#13](https://github.com/nightBaker/fleans/tree/main/tests/manual/13-multi-instance/) (E2E `MultiInstanceTests`, `MultiInstanceScopeTests`) | `loopCardinality`, `collection` and `completionCondition` supported; the `nrOf*` counters are visible only to `completionCondition`. Multi-instance Call Activity does not complete yet — [#782](https://github.com/nightBaker/fleans/issues/782). See [Multi-Instance Activities](/fleans/guides/multi-instance-activities/). |

### Gateways

| Element | BPMN XML | Status | Parser | Tested by | Notes |
|---|---|---|---|---|---|
| Exclusive | `<bpmn:exclusiveGateway>` | ✅ | `ParseActivities` | [#03](https://github.com/nightBaker/fleans/tree/main/tests/manual/03-exclusive-gateway/) | XOR — first true condition wins; default flow as fallback. |
| Inclusive | `<bpmn:inclusiveGateway>` | ✅ | `ParseActivities` | [#14](https://github.com/nightBaker/fleans/tree/main/tests/manual/14-inclusive-gateway/) | OR — every true branch fires; join syncs all live tokens. |
| Parallel | `<bpmn:parallelGateway>` | ✅ | `ParseActivities` | [#04](https://github.com/nightBaker/fleans/tree/main/tests/manual/04-parallel-gateway/) | AND — fork all branches; join waits for all. |
| Complex | `<bpmn:complexGateway>` | ✅ | `ParseActivities` | [#20-cg](https://github.com/nightBaker/fleans/tree/main/tests/manual/20-complex-gateway/) | Conditional outgoing flows + optional `activationCondition` on join. |
| Event-Based | `<bpmn:eventBasedGateway>` | ✅ | `ParseActivities` | [#05](https://github.com/nightBaker/fleans/tree/main/tests/manual/05-event-based-gateway/) | First arriving event wins; loser subscriptions cancelled. |

### Connecting Objects

| Element | BPMN XML | Status | Parser | Tested by | Notes |
|---|---|---|---|---|---|
| Sequence Flow | `<bpmn:sequenceFlow>` | ✅ | `ParseSequenceFlows` | every fixture | Every flow needs an `id`. `<conditionExpression>` and `default="…"` are honoured on flows leaving a gateway. A task or event follows only its **first** outgoing flow: implicit splits and conditions on its other flows are ignored ([#856](https://github.com/nightBaker/fleans/issues/856)) — use a gateway. See [Gateways, Timers and Signals](/fleans/guides/gateways-and-conditions/). |
| Message Flow | `<bpmn:messageFlow>` | ❌ | — | — | Cross-pool messaging not implemented. |
| Association | `<bpmn:association>` | ❌ | — | — | Annotation-to-flow links not parsed. |
| Data Association | `<bpmn:dataInputAssociation>` / `<bpmn:dataOutputAssociation>` | ❌ | — | — | Data-object flow links not parsed. |

### Swimlanes and Artifacts

| Element | BPMN XML | Status | Parser | Tested by | Notes |
|---|---|---|---|---|---|
| Pool | `<bpmn:participant>` | ⚠️ | `BpmnConverter.SelectProcess` | `CollaborationParsingTests` | A collaboration deploys its executable process (`isExecutable="true"`, else the pool with a start event); the other pools are documentation. No cross-pool execution. |
| Lane | `<bpmn:lane>` | ⚠️ | — | `CollaborationParsingTests` | Accepted and kept in the BPMN; no lane-based assignment at runtime. |
| Data Object | `<bpmn:dataObject>` | ⚠️ | — | `CollaborationParsingTests` | Accepted (with data associations) and kept in the BPMN; no runtime semantics — variables use `_context`. |
| Data Store | `<bpmn:dataStoreReference>` | ⚠️ | — | `CollaborationParsingTests` | Accepted and kept in the BPMN; external data stores are not modeled at runtime. |
| Group | `<bpmn:group>` | ❌ | — | — | Visual-only; no semantic meaning needed. |
| Annotation | `<bpmn:textAnnotation>` | ❌ | — | — | Visual-only; preserved on round-trip but ignored at runtime. |

## Notes that span multiple rows

- **Multi-Instance** can wrap any task or sub-process — see the [Multi-Instance Activities](/fleans/guides/multi-instance-activities/) guide.
- **Transaction Sub-Process** all three terminal outcomes (Completed / Cancelled / Hazard) are supported — see the [Transaction Sub-Process status](#transaction-sub-process-status) section under Cancel Events for semantics details.
- **Conditional Events** (start, intermediate catch, boundary) share the same evaluation engine — see the [Conditional Events](#conditional-events) section.
- **Event Sub-Processes** support four trigger types (error / timer / message / signal) in both interrupting and non-interrupting variants. Error sub-processes are always interrupting per BPMN 2.0 §10.2.4. Escalation-triggered event sub-processes are not supported yet ([#783](https://github.com/nightBaker/fleans/issues/783)).
- **Compensation Events** (boundary, intermediate throw, end event) — see the [Compensation Events](#compensation-events) section for execution-order rules.
- **Input/Output Mapping** (`fleans:ioMapping` / `zeebe:ioMapping`) with C# expressions works on tasks, sub-processes, call activities and events, on the same elements Camunda 8 allows. See [Input/Output Mapping](/fleans/concepts/activities/io-mapping/).
- **Escalation Events** propagate from child to parent scope automatically; specific escalation codes match before catch-all (null code) boundaries; uncaught escalations are non-faulting per BPMN spec.

## Conditional Events

Conditional events allow workflow execution to react to data-driven conditions. Conditions are C# expressions evaluated against the current workflow variables.

Write conditions the same way as sequence-flow `<conditionExpression>`s: reference variables by bare name (`amount > 500`, `status == "approved"`) or with `${...}` placeholders (`${amount > 500}`). At deploy time the engine rewrites variable references to `_context.<name>`; already-prefixed `_context.amount` is left as-is. Names inside string literals are not rewritten. The deploy then compiles the condition: a syntax error rejects the deploy with the element id.

### Supported types

| Element | BPMN XML | Behavior |
|---------|----------|----------|
| **Conditional Start Event** | `<startEvent><conditionalEventDefinition><condition>expr</condition></conditionalEventDefinition></startEvent>` | Creates a new workflow instance when the condition evaluates to `true`. Triggered via the `POST /Execution/evaluate-conditions` API endpoint. |
| **Conditional Intermediate Catch Event** | `<intermediateCatchEvent><conditionalEventDefinition><condition>expr</condition></conditionalEventDefinition></intermediateCatchEvent>` | Blocks the sequence flow until the condition becomes `true`. The condition is re-evaluated whenever another activity in the same instance completes. |
| **Conditional Boundary Event (Interrupting)** | `<boundaryEvent attachedToRef="task" cancelActivity="true"><conditionalEventDefinition><condition>expr</condition></conditionalEventDefinition></boundaryEvent>` | Cancels the host activity and follows the boundary path when the condition becomes `true` — subject to the [scope caveat](#how-conditions-are-evaluated) below. |
| **Conditional Boundary Event (Non-Interrupting)** | `<boundaryEvent attachedToRef="task" cancelActivity="false"><conditionalEventDefinition>...</conditionalEventDefinition></boundaryEvent>` | Fires the boundary path when the condition transitions from `false` to `true` (edge-triggered), but the host activity continues running — subject to the [scope caveat](#how-conditions-are-evaluated) below ([#784](https://github.com/nightBaker/fleans/issues/784)). |

### How conditions are evaluated

Conditions are registered as **watchers** when their host element starts executing. Watchers are evaluated in the workflow's execution loop whenever at least one activity completes. This completion-driven evaluation means:

- **Intermediate catch events** block until another activity completes and the condition is `true` at that point.
- Conditions are evaluated against the **watcher's own variable scope**. Parallel-gateway branches (and boundary paths) receive a *cloned copy* of the variables, so variables written by a sibling branch — including variables passed to `POST /Execution/complete-activity` for a task on another branch — are not visible to the watcher; that completion only triggers re-evaluation. There is currently no API to change the variables of a waiting branch.
- **Boundary events** require a concurrent activity (e.g., via a parallel gateway fork) to complete, triggering the watcher evaluation while the host task is still active.
- **Non-interrupting boundaries** use edge detection: they fire only when the condition *transitions* from `false` to `true`, preventing repeated firing on every evaluation cycle.

### Conditional Start Events

Conditional start events are evaluated externally via the REST API:

```bash
curl -X POST http://localhost:8081/Execution/evaluate-conditions \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"my-process","Variables":{"threshold":100}}'
```

The engine evaluates the condition for each registered conditional start event listener. If the condition is `true` and the process definition is active, a new workflow instance is created with the supplied variables. The response includes the IDs of any started instances and any errors encountered.

## Compensation Events

Compensation allows a workflow to undo already-completed work by running dedicated **handler activities** in reverse completion order.

<figure class="arch-diagram" style="--arch-ratio: 720 / 546">
  <iframe data-arch-src="/fleans/diagrams/error-boundary-flow.html" src="/fleans/diagrams/error-boundary-flow.html?embed=1" title="Error boundary matching and compensation flow" loading="lazy"></iframe>
  <figcaption>Where compensation fits: after an error is handled, a compensate throw undoes completed compensable activities in reverse order. <a href="/fleans/diagrams/error-boundary-flow.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

### Why compensation exists

In long-running business processes (e.g., travel booking: reserve hotel → book flight) you may need to roll back successfully completed steps when a later step fails — or as an explicit business decision. BPMN models this with compensation rather than transactions because the activities may span external systems and cannot be rolled back atomically.

### How to use it

1. **Mark activities as compensable** — attach a `CompensationBoundaryEvent` to any script task or service task you want to be able to undo. Write `cancelActivity="false"` on it: a boundary without the attribute is interrupting by default, and the deploy rejects an interrupting compensation boundary.

2. **Wire a handler** — draw an `<association>` from the boundary event (`sourceRef`) to a handler script task (`targetRef`, the task that performs the undo logic). The parser reads only `sourceRef` / `targetRef`; `associationDirection="One"` is what modelers write but is not checked.

3. **Trigger compensation** — place a `CompensationIntermediateThrowEvent` on the main flow where you want compensation to happen, or end the process with a `CompensationEndEvent`.

### Broadcast vs targeted compensation

| Event | What runs |
|-------|-----------|
| `<compensateEventDefinition />` (no `activityRef`) | All compensable activities in scope, in reverse completion order |
| `<compensateEventDefinition activityRef="task_id" />` | Only the handler for the named activity |

### Execution order

Handlers execute **in reverse completion order** — the most recently completed activity is compensated first. This mirrors how a stack-based undo works and is the BPMN 2.0 default.

### Best-practice example

```xml
<!-- Activities to compensate -->
<scriptTask id="reserve_hotel" name="Reserve Hotel" scriptFormat="csharp">
  <script>_context.hotelStatus = "reserved"</script>
</scriptTask>
<scriptTask id="book_flight" name="Book Flight" scriptFormat="csharp">
  <script>_context.flightStatus = "booked"</script>
</scriptTask>

<!-- Compensation boundary events (non-interrupting) -->
<boundaryEvent id="cb_hotel" attachedToRef="reserve_hotel" cancelActivity="false">
  <compensateEventDefinition />
</boundaryEvent>
<boundaryEvent id="cb_flight" attachedToRef="book_flight" cancelActivity="false">
  <compensateEventDefinition />
</boundaryEvent>

<!-- Handler tasks -->
<scriptTask id="cancel_hotel" name="Cancel Hotel" scriptFormat="csharp">
  <script>_context.hotelStatus = "cancelled"</script>
</scriptTask>
<scriptTask id="cancel_flight" name="Cancel Flight" scriptFormat="csharp">
  <script>_context.flightStatus = "cancelled"</script>
</scriptTask>

<!-- Associations (boundary → handler) -->
<association id="a1" sourceRef="cb_hotel" targetRef="cancel_hotel" associationDirection="One" />
<association id="a2" sourceRef="cb_flight" targetRef="cancel_flight" associationDirection="One" />

<!-- Broadcast compensation throw on the main flow -->
<intermediateThrowEvent id="compensate_all" name="Compensate All">
  <compensateEventDefinition />
</intermediateThrowEvent>
```

When the workflow reaches `compensate_all`, the engine:
1. Runs `cancel_flight` (book_flight completed last → compensated first)
2. Runs `cancel_hotel`
3. Resumes the main flow at the next element after `compensate_all`

### Notes

- Handler activities are **not on the main sequence flow** — they are wired only via `<association>`. Do not draw sequence flows to/from handlers.
- A `CompensationEndEvent` also triggers compensation but ends the process (no resumption after the walk). It is typically used inside an error sub-process or error boundary handler.
- Compensation state (`CompensationLog`, `ActiveCompensationWalk`) is rebuilt from domain events on grain activation — it is **not stored in the relational database** directly.

### Visualising the compensation log in the admin UI

Open the instance in the Admin UI (`/process-instance/{instanceId}`) and expand a compensable activity's entry in the **Journal**. Its details show a **Compensation** section:

- the completion **sequence**, whether it is **compensated** or still **pending**, and the **handler** that compensated it;
- the **variables** captured when the activity completed — the values its handler sees.

There is no auto-refresh — press **Refresh** to take a fresh snapshot.

The page fetches the log via the `ICompensationLogService` application service which activates the workflow grain on every call. It is intended for admin-UI inspection only — list views and analytics endpoints continue reading from the EF projection.

## Cancel Events

### Transaction Sub-Process status

:::note[All three Transaction outcomes are supported]
- ✅ **Completed outcome** — normal end event reached. Variables merge into the parent scope. **Fully supported.**
- ✅ **Cancelled outcome** — `<bpmn:cancelEndEvent>` reached inside the `<bpmn:transaction>`. Active siblings cancel; compensation handlers run in reverse completion order; the Cancel Boundary Event fires. **Fully supported** (regression test [#36](https://github.com/nightBaker/fleans/blob/main/tests/manual/30-cancel-event/test-plan.md)).
- ✅ **Hazard outcome** — an unhandled error escapes the transaction scope (BPMN §10.4.3). Active siblings are cancelled; compensation handlers run for completed compensable activities; an Error Boundary Event on the TX host fires (or the TX host itself fails if no boundary is defined). **Fully supported** (manual test [#26b](https://github.com/nightBaker/fleans/blob/main/tests/manual/26-transaction-subprocess/test-plan-hazard.md), implemented in [#492](https://github.com/nightBaker/fleans/issues/492)).

**Hazard semantics:** when an unhandled exception escapes a transaction, the engine:
1. Sets the transaction outcome to **Hazard** and cancels remaining active siblings.
2. Runs the compensation walk for any compensable activities that completed before the failure.
3. Once compensation finishes, activates the Error Boundary Event attached to the TX host (if present), or fails the TX host to propagate the error to the parent scope.

**Constraint:** **Multi-instance transactions** are rejected at parse time (the deploy fails with `Transaction Sub-Process '<id>' does not support multi-instance loop characteristics`). This is a deliberate restriction, not a bug — multi-instance + atomicity has subtle interactions with compensation walk ordering that #307 will address.

**Nested transactions:** supported on both the happy path and the inner-cancel path — an inner transaction can reach a Cancel End Event, run its compensation handlers, and exit through its Cancel Boundary Event while the outer transaction carries on and commits. Covered by manual test plan [#53](https://github.com/nightBaker/fleans/blob/main/tests/manual/53-nested-transaction/test-plan.md) (Scenarios A and B are also automated E2E).
:::

Cancel events implement the **transaction cancellation** path in BPMN: when a Cancel End Event fires inside a Transaction Sub-Process, the engine rolls back the transaction's scope and routes execution to a recovery flow via the Cancel Boundary Event.

### Why cancel events exist

Transactions model atomic business operations (e.g., payment processing). If an internal check decides the transaction must not commit — and the decision is a normal business outcome rather than a system fault — the process reaches a **Cancel End Event** to signal deliberate cancellation. Unlike error events, cancellation is not a failure; it is a designed exit from the transaction with a dedicated recovery path.

### How to use it

1. **Place a Cancel End Event inside a Transaction** — draw a `<cancelEndEvent>` at the end of a flow branch inside `<transaction>`. When the engine reaches it, all still-active activities in the transaction scope are cancelled, and any compensation handlers (if defined) are run in reverse completion order.

2. **Attach a Cancel Boundary Event to the transaction** — draw a `<boundaryEvent cancelActivity="true">` with `<cancelEventDefinition />` on the outer edge of the `<transaction>` element. This event fires after compensation finishes, routing the process to the recovery flow.

3. **Wire the recovery flow** — connect the Cancel Boundary Event to the activities that handle the cancelled outcome (e.g., notify the user, roll back external state).

### Interaction with compensation

Cancel events and compensation work together. If you attach `CompensationBoundaryEvent`s to activities inside the transaction, the engine will run their handlers (in reverse completion order) **before** firing the Cancel Boundary Event. This gives you a deterministic cleanup sequence:

1. Cancel active activities in the transaction scope
2. Run compensation handlers in reverse completion order
3. Fire Cancel Boundary Event → execute recovery flow

### Best-practice example

```xml
<transaction id="paymentTransaction" name="Payment Transaction">
  <startEvent id="tx_start" />

  <!-- Compensable task with a handler -->
  <userTask id="reserve" name="Reserve Funds" />
  <boundaryEvent id="cb_reserve" attachedToRef="reserve" cancelActivity="false">
    <compensateEventDefinition />
  </boundaryEvent>
  <scriptTask id="release_reserve" name="Release Reserve" scriptFormat="csharp">
    <script>_context.released = true</script>
  </scriptTask>
  <association id="a1" sourceRef="cb_reserve" targetRef="release_reserve" associationDirection="One" />

  <!-- Cancel decision -->
  <exclusiveGateway id="gw" default="toAccepted" />
  <cancelEndEvent id="cancel_end" name="Payment Rejected" />
  <endEvent id="tx_end" name="Payment Accepted" />

  <sequenceFlow id="toReserve" sourceRef="tx_start" targetRef="reserve" />
  <sequenceFlow id="toGateway" sourceRef="reserve" targetRef="gw" />
  <sequenceFlow id="toRejected" sourceRef="gw" targetRef="cancel_end">
    <conditionExpression>rejected == true</conditionExpression>
  </sequenceFlow>
  <sequenceFlow id="toAccepted" sourceRef="gw" targetRef="tx_end" />
</transaction>

<!-- Cancel Boundary Event on the transaction -->
<boundaryEvent id="cancel_boundary" attachedToRef="paymentTransaction" cancelActivity="true">
  <cancelEventDefinition />
</boundaryEvent>

<!-- Recovery flow -->
<scriptTask id="notify_rejected" name="Notify Rejection" scriptFormat="csharp">
  <script>_context.status = "rejected"</script>
</scriptTask>
<endEvent id="end" />

<sequenceFlow id="toNotify" sourceRef="cancel_boundary" targetRef="notify_rejected" />
<sequenceFlow id="toEnd" sourceRef="notify_rejected" targetRef="end" />
```

When the workflow reaches `cancel_end`:
1. `release_reserve` (compensation handler for `reserve`) runs
2. `cancel_boundary` fires
3. `notify_rejected` executes
4. Process ends

### Notes

- A Cancel End Event is only valid inside a `<transaction>`; the deploy rejects one anywhere else. A Cancel Boundary Event may only be attached to a `<transaction>`.
- Every `<sequenceFlow>` needs an `id`. A condition is a plain expression (`rejected == true`) — don't prefix it with `=`. An exclusive gateway with conditional flows ignores an outgoing flow that has neither a condition nor the gateway's `default="…"`, which is why `toAccepted` is the default flow.
- A Cancel Boundary Event (`cancelActivity="true"`) is always interrupting — it always cancels the transaction scope.
- The transaction outcome is recorded as `Cancelled` in the engine state (queryable via the grain interface) and can be used for audit or downstream decisions.
- Compensation handlers inside the transaction run in the same isolated-scope model as standalone compensation events — each handler gets a fresh child variable scope seeded with the compensable activity's completion-time snapshot.
