---
title: BPMN Support
description: Canonical coverage matrix for BPMN 2.0 elements in Fleans.
---

{/* drift-guard:
  Parent foreach handlers in src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs:
    :94 (startEvent), :153 (intermediateCatchEvent), :206 (intermediateThrowEvent),
    :254 (endEvent), :303 (task), :313 (userTask), :333 (serviceTask), :354 (scriptTask),
    :369 (exclusiveGateway), :382 (parallelGateway), :420 (inclusiveGateway),
    :459 (complexGateway), :507 (eventBasedGateway), :517 (subProcess),
    :574 (transaction), :601 (callActivity), :638 (boundaryEvent), :845 (ParseSequenceFlows)
  Child event-definition / loop / attribute detections — same file:
    :107,113,120,129,132,536 (start-event variants),
    :165,166,167,191 (intermediate-catch variants),
    :209,225,226 (intermediate-throw variants),
    :259,263,271 (end-event variants),
    :527-530 (event-sub-process triggeredByEvent),
    :578 (transaction multi-instance reject),
    :661,672,689,690,691,692,693,727 (boundary variants),
    :1132,1138 (multi-instance loopCharacteristics + loopCardinality)
  pinned at branch=docs/404-bpmn-matrix; refresh if any of the above change */}

This page is the canonical coverage matrix for BPMN 2.0 elements in Fleans. Every row is pinned to the parser code that handles it (`Fleans.Infrastructure/Bpmn/BpmnConverter.cs`) and the manual fixture that exercises it end-to-end (`tests/manual/NN-*/`).

## Status legend

| Emoji | Meaning |
|---|---|
| ✅ | Fully supported — parse handler + Activity class + tested end-to-end. |
| ⚠️ | Partial support — engine works but with caveats: (a) sub-features pending in a follow-up issue, OR (b) BPMN editor's properties panel may not expose this variant. Status reflects engine support; editor UI is a separate concern. |
| 🚧 | In progress / planned, tracking issue open. |
| ❌ | Not implemented. |

## Coverage matrix

### Start Events

| Element | BPMN XML | Status | Source pin | Tested by | Notes |
|---|---|---|---|---|---|
| Plain Start Event | `<bpmn:startEvent>` | ✅ | `BpmnConverter.cs:94` | [#01](../../../tests/manual/01-basic-workflow/) | Default start of any process. |
| Timer Start Event | `<bpmn:startEvent><timerEventDefinition>` | ✅ | `BpmnConverter.cs:94 + :107` | unit tests only | Schedules a process instance via `RegisterOrUpdateReminder`. |
| Message Start Event | `<bpmn:startEvent><messageEventDefinition>` | ✅ | `BpmnConverter.cs:94 + :113` | [#16](../../../tests/manual/16-message-start-event/) | Auto-creates an instance on matching message delivery. |
| Signal Start Event | `<bpmn:startEvent><signalEventDefinition>` | ✅ | `BpmnConverter.cs:94 + :120` | [#17](../../../tests/manual/17-signal-start-event/) | Broadcast match creates an instance. |
| Error Start Event *(Event Sub-Process only)* | `<bpmn:startEvent><errorEventDefinition>` | ✅ | `BpmnConverter.cs:94 + :132, :536` | [#19](../../../tests/manual/19-event-subprocess-error/) | Only valid inside `triggeredByEvent="true"` sub-processes. |
| Conditional Start Event | `<bpmn:startEvent><conditionalEventDefinition>` | ✅ | `BpmnConverter.cs:94 + :129` | [#24-conditional](../../../tests/manual/24-conditional-event/) | Triggered via `POST /Workflow/evaluate-conditions`. |
| Multiple Start Event | `<bpmn:startEvent>` with multiple event definitions | ✅ | `BpmnConverter.cs:94` (multi-def detection) | [#24-multiple](../../../tests/manual/24-multiple-event/) | First-fires-wins; surplus subscriptions cancelled. |

### Intermediate Catch Events

| Element | BPMN XML | Status | Source pin | Tested by | Notes |
|---|---|---|---|---|---|
| Message Intermediate Catch | `<bpmn:intermediateCatchEvent><messageEventDefinition>` | ✅ | `BpmnConverter.cs:153 + :166` | [#09](../../../tests/manual/09-message-events/) | Correlates by `=variable` extension. |
| Signal Intermediate Catch | `<bpmn:intermediateCatchEvent><signalEventDefinition>` | ✅ | `BpmnConverter.cs:153 + :167` | [#10](../../../tests/manual/10-signal-events/) | Broadcast match unblocks. |
| Timer Intermediate Catch | `<bpmn:intermediateCatchEvent><timerEventDefinition>` | ✅ | `BpmnConverter.cs:153 + :165` | [#08](../../../tests/manual/08-timer-events/) | ISO 8601 duration / cycle / date. |
| Conditional Intermediate Catch | `<bpmn:intermediateCatchEvent><conditionalEventDefinition>` | ✅ | `BpmnConverter.cs:153 + :191` | [#24-conditional](../../../tests/manual/24-conditional-event/) | Re-evaluated on every activity completion. |
| Multiple Intermediate Catch | `<bpmn:intermediateCatchEvent>` w/ multiple event-defs | ✅ | `BpmnConverter.cs:153` (multi-def) | [#24-multiple](../../../tests/manual/24-multiple-event/) | First-fires-wins. |

### Intermediate Throw Events

| Element | BPMN XML | Status | Source pin | Tested by | Notes |
|---|---|---|---|---|---|
| Signal Intermediate Throw | `<bpmn:intermediateThrowEvent><signalEventDefinition>` | ✅ | `BpmnConverter.cs:206 + :225` | [#10](../../../tests/manual/10-signal-events/) | Broadcasts cluster-wide. |
| Escalation Intermediate Throw | `<bpmn:intermediateThrowEvent><escalationEventDefinition>` | ✅ | `BpmnConverter.cs:206 + :226` | [#24-escalation](../../../tests/manual/24-escalation-event/) | Mid-flow throw, continues execution after. |
| Compensation Intermediate Throw | `<bpmn:intermediateThrowEvent><compensateEventDefinition>` | ✅ | `BpmnConverter.cs:206 + :209` | [#24-compensation](../../../tests/manual/24-compensation-event/) | Broadcast or targeted (`activityRef`). |
| Multiple Intermediate Throw | `<bpmn:intermediateThrowEvent>` w/ multiple event-defs | ✅ | `BpmnConverter.cs:206` (multi-def) | [#24-multiple](../../../tests/manual/24-multiple-event/) | Fires every defined signal. |

### End Events

| Element | BPMN XML | Status | Source pin | Tested by | Notes |
|---|---|---|---|---|---|
| Plain End Event | `<bpmn:endEvent>` | ✅ | `BpmnConverter.cs:254` | [#01](../../../tests/manual/01-basic-workflow/) | Terminates the enclosing scope. |
| Error End Event | `<bpmn:endEvent><errorEventDefinition>` | ✅ | `BpmnConverter.cs:254` (default error path) | [#11](../../../tests/manual/11-error-boundary/) | Throws to nearest matching error boundary. |
| Cancel End Event *(Transaction only)* | `<bpmn:endEvent><cancelEventDefinition>` | ✅ | `BpmnConverter.cs:254 + :271` | [#30-cancel](../../../tests/manual/30-cancel-event/) | Triggers transaction Cancel boundary. |
| Compensation End Event | `<bpmn:endEvent><compensateEventDefinition>` | ✅ | `BpmnConverter.cs:254 + :259` | [#24-compensation](../../../tests/manual/24-compensation-event/) | Broadcasts compensation throw on terminate. |
| Escalation End Event | `<bpmn:endEvent><escalationEventDefinition>` | ✅ | `BpmnConverter.cs:254 + :263` | [#24-escalation](../../../tests/manual/24-escalation-event/) | Throws escalation; uncaught escalation is non-faulting. |

### Boundary Events

| Element | BPMN XML | Status | Source pin | Tested by | Notes |
|---|---|---|---|---|---|
| Error Boundary | `<bpmn:boundaryEvent><errorEventDefinition>` | ✅ | `BpmnConverter.cs:638 + :690` | [#11](../../../tests/manual/11-error-boundary/) | Always interrupting per BPMN spec. Optional `errorRef` filters by code. |
| Timer Boundary | `<bpmn:boundaryEvent><timerEventDefinition>` | ✅ | `BpmnConverter.cs:638 + :689` | [#15](../../../tests/manual/15-non-interrupting-boundaries/) | Interrupting + non-interrupting variants. Cycle timers re-register on non-interrupting. |
| Message Boundary | `<bpmn:boundaryEvent><messageEventDefinition>` | ⚠️ | `BpmnConverter.cs:638 + :691` | [#09](../../../tests/manual/09-message-events/) | **KNOWN BUG:** boundary events on `IntermediateCatchEvent` don't register subscriptions. |
| Signal Boundary | `<bpmn:boundaryEvent><signalEventDefinition>` | ⚠️ | `BpmnConverter.cs:638 + :692` | [#10](../../../tests/manual/10-signal-events/) | Same KNOWN BUG as Message Boundary. |
| Escalation Boundary | `<bpmn:boundaryEvent><escalationEventDefinition>` | ✅ | `BpmnConverter.cs:638 + :693` | [#24-escalation](../../../tests/manual/24-escalation-event/) | Interrupting + non-interrupting variants. Specific code matches take priority over catch-all. |
| Compensation Boundary | `<bpmn:boundaryEvent><compensateEventDefinition>` | ✅ | `BpmnConverter.cs:638 + :661` | [#24-compensation](../../../tests/manual/24-compensation-event/) | Attached to compensable activity. |
| Conditional Boundary | `<bpmn:boundaryEvent><conditionalEventDefinition>` | ✅ | `BpmnConverter.cs:638 + :672` | [#24-conditional](../../../tests/manual/24-conditional-event/) | Interrupting + non-interrupting; non-interrupting is edge-triggered. |
| Cancel Boundary *(Transaction only)* | `<bpmn:boundaryEvent><cancelEventDefinition>` | ✅ | `BpmnConverter.cs:638 + :727` | [#30-cancel](../../../tests/manual/30-cancel-event/) | Always interrupting. Fires when Cancel End Event executes. |
| Multiple Boundary | `<bpmn:boundaryEvent>` w/ multiple event-defs | ✅ | `BpmnConverter.cs:638` (multi-def) | [#24-multiple](../../../tests/manual/24-multiple-event/) | First-fires-wins cancels host activity. |

### Tasks ([details](./activities/tasks))

| Element | BPMN XML | Status | Source pin | Tested by | Notes |
|---|---|---|---|---|---|
| Plain Task | `<bpmn:task>` | ✅ | `BpmnConverter.cs:303` | [#01](../../../tests/manual/01-basic-workflow/) | Auto-completes immediately (no executor). |
| Script Task | `<bpmn:scriptTask scriptFormat="csharp">` | ✅ | `BpmnConverter.cs:354` | [#02](../../../tests/manual/02-script-tasks/) | DynamicExpresso expressions on `_context`. |
| Service Task / Custom Task | `<bpmn:serviceTask type="…">` | ✅ | `BpmnConverter.cs:333` | [#37](../../../tests/manual/37-custom-task-framework/), [#39](../../../tests/manual/39-rest-caller/) | Plugin-based execution. |
| User Task | `<bpmn:userTask>` | ✅ | `BpmnConverter.cs:313` | [#19-user-task](../../../tests/manual/18-user-task/) | Claim/unclaim/complete lifecycle. |
| Call Activity | `<bpmn:callActivity calledElement="…">` | ✅ | `BpmnConverter.cs:601` | [#06](../../../tests/manual/06-call-activity/) | Cross-process variable mapping. |

### Sub-Processes

| Element | BPMN XML | Status | Source pin | Tested by | Notes |
|---|---|---|---|---|---|
| Embedded Sub-Process | `<bpmn:subProcess>` | ✅ | `BpmnConverter.cs:517` | [#07](../../../tests/manual/07-subprocess/) | Own start/end, isolated variable scope. |
| Event Sub-Process | `<bpmn:subProcess triggeredByEvent="true">` | ✅ | `BpmnConverter.cs:517 + :527-530` | [#19](../../../tests/manual/19-event-subprocess-error/), [#20-tm](../../../tests/manual/20-event-subprocess-timer/), [#21](../../../tests/manual/21-event-subprocess-message/), [#22](../../../tests/manual/22-event-subprocess-signal/), [#23](../../../tests/manual/23-event-subprocess-non-interrupting/) | Error-/timer-/message-/signal-triggered; interrupting + non-interrupting. |
| Transaction Sub-Process | `<bpmn:transaction>` | ✅ | `BpmnConverter.cs:574 + :578` | [#26](../../../tests/manual/26-transaction-subprocess/), [#30-cancel](../../../tests/manual/30-cancel-event/), [#53-nested](../../../tests/manual/53-nested-transaction/) | Completed ✅, Cancelled ✅, Hazard ✅. All three terminal outcomes supported. See [Transaction Sub-Process status](#transaction-sub-process-status). |
| Multi-Instance (any host) | `<bpmn:*><multiInstanceLoopCharacteristics>` | ⚠️ | `BpmnConverter.cs:1132 + :1138` | [#13](../../../tests/manual/13-multi-instance/) | `loopCardinality` + `inputCollection` supported; `completionCondition` and `nrOf*` pending [#470](https://github.com/nightBaker/fleans/issues/470). |

### Gateways

| Element | BPMN XML | Status | Source pin | Tested by | Notes |
|---|---|---|---|---|---|
| Exclusive | `<bpmn:exclusiveGateway>` | ✅ | `BpmnConverter.cs:369` | [#03](../../../tests/manual/03-exclusive-gateway/) | XOR — first true condition wins; default flow as fallback. |
| Inclusive | `<bpmn:inclusiveGateway>` | ✅ | `BpmnConverter.cs:420` | [#14](../../../tests/manual/14-inclusive-gateway/) | OR — every true branch fires; join syncs all live tokens. |
| Parallel | `<bpmn:parallelGateway>` | ✅ | `BpmnConverter.cs:382` | [#04](../../../tests/manual/04-parallel-gateway/) | AND — fork all branches; join waits for all. |
| Complex | `<bpmn:complexGateway>` | ✅ | `BpmnConverter.cs:459` | [#20-cg](../../../tests/manual/20-complex-gateway/) | Conditional outgoing flows + optional `activationCondition` on join. |
| Event-Based | `<bpmn:eventBasedGateway>` | ✅ | `BpmnConverter.cs:507` | [#05](../../../tests/manual/05-event-based-gateway/) | First arriving event wins; loser subscriptions cancelled. |

### Connecting Objects

| Element | BPMN XML | Status | Source pin | Tested by | Notes |
|---|---|---|---|---|---|
| Sequence Flow | `<bpmn:sequenceFlow>` | ✅ | `BpmnConverter.cs:845` (`ParseSequenceFlows`) | every fixture | Optional `<conditionExpression>` for guarded flows. |
| Message Flow | `<bpmn:messageFlow>` | ❌ | — | — | Cross-pool messaging not implemented. |
| Association | `<bpmn:association>` | ❌ | — | — | Annotation-to-flow links not parsed. |
| Data Association | `<bpmn:dataInputAssociation>` / `<bpmn:dataOutputAssociation>` | ❌ | — | — | Data-object flow links not parsed. |

### Swimlanes and Artifacts

| Element | BPMN XML | Status | Source pin | Tested by | Notes |
|---|---|---|---|---|---|
| Pool | `<bpmn:participant>` | ❌ | — | — | Multi-pool processes not supported. |
| Lane | `<bpmn:lane>` | ❌ | — | — | Lane partitioning not parsed. |
| Data Object | `<bpmn:dataObject>` | ❌ | — | — | Variables use `_context` instead. |
| Data Store | `<bpmn:dataStoreReference>` | ❌ | — | — | External data stores not modeled. |
| Group | `<bpmn:group>` | ❌ | — | — | Visual-only; no semantic meaning needed. |
| Annotation | `<bpmn:textAnnotation>` | ❌ | — | — | Visual-only; preserved on round-trip but ignored at runtime. |

## Notes that span multiple rows

- **Multi-Instance** can wrap any task or sub-process — see the [Multi-Instance Activities](/fleans/guides/multi-instance-activities/) guide.
- **Transaction Sub-Process** all three terminal outcomes (Completed / Cancelled / Hazard) are supported — see the [Transaction Sub-Process status](#transaction-sub-process-status) section under Cancel Events for semantics details.
- **Conditional Events** (start, intermediate catch, boundary) share the same evaluation engine — see the [Conditional Events](#conditional-events) section.
- **Event Sub-Processes** support all four trigger types (error / timer / message / signal) in both interrupting and non-interrupting variants. Error sub-processes are always interrupting per BPMN 2.0 §10.2.4.
- **Compensation Events** (boundary, intermediate throw, end event) — see the [Compensation Events](#compensation-events) section for execution-order rules.
- **Escalation Events** propagate from child to parent scope automatically; specific escalation codes match before catch-all (null code) boundaries; uncaught escalations are non-faulting per BPMN spec.

## Conditional Events

Conditional events allow workflow execution to react to data-driven conditions. Conditions are C# expressions evaluated against the current workflow variables.

### Supported types

| Element | BPMN XML | Behavior |
|---------|----------|----------|
| **Conditional Start Event** | `<startEvent><conditionalEventDefinition><condition>expr</condition></conditionalEventDefinition></startEvent>` | Creates a new workflow instance when the condition evaluates to `true`. Triggered via the `POST /Workflow/evaluate-conditions` API endpoint. |
| **Conditional Intermediate Catch Event** | `<intermediateCatchEvent><conditionalEventDefinition><condition>expr</condition></conditionalEventDefinition></intermediateCatchEvent>` | Blocks the sequence flow until the condition becomes `true`. The condition is re-evaluated whenever another activity in the same instance completes. |
| **Conditional Boundary Event (Interrupting)** | `<boundaryEvent attachedToRef="task" cancelActivity="true"><conditionalEventDefinition><condition>expr</condition></conditionalEventDefinition></boundaryEvent>` | Cancels the host activity and follows the boundary path when the condition becomes `true`. |
| **Conditional Boundary Event (Non-Interrupting)** | `<boundaryEvent attachedToRef="task" cancelActivity="false"><conditionalEventDefinition>...</conditionalEventDefinition></boundaryEvent>` | Fires the boundary path when the condition transitions from `false` to `true` (edge-triggered), but the host activity continues running. |

### How conditions are evaluated

Conditions are registered as **watchers** when their host element starts executing. Watchers are evaluated in the workflow's execution loop whenever at least one activity completes. This completion-driven evaluation means:

- **Intermediate catch events** block until another activity completes and the condition is `true` at that point.
- **Boundary events** require a concurrent activity (e.g., via a parallel gateway fork) to complete, triggering the watcher evaluation while the host task is still active.
- **Non-interrupting boundaries** use edge detection: they fire only when the condition *transitions* from `false` to `true`, preventing repeated firing on every evaluation cycle.

### Conditional Start Events

Conditional start events are evaluated externally via the REST API:

```bash
curl -k -X POST https://localhost:7140/Workflow/evaluate-conditions \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"my-process","Variables":{"threshold":100}}'
```

The engine evaluates the condition for each registered conditional start event listener. If the condition is `true` and the process definition is active, a new workflow instance is created with the supplied variables. The response includes the IDs of any started instances and any errors encountered.

## Compensation Events

Compensation allows a workflow to undo already-completed work by running dedicated **handler activities** in reverse completion order.

### Why compensation exists

In long-running business processes (e.g., travel booking: reserve hotel → book flight) you may need to roll back successfully completed steps when a later step fails — or as an explicit business decision. BPMN models this with compensation rather than transactions because the activities may span external systems and cannot be rolled back atomically.

### How to use it

1. **Mark activities as compensable** — attach a `CompensationBoundaryEvent` to any script task or service task you want to be able to undo. The boundary event is non-interrupting (`cancelActivity="false"`).

2. **Wire a handler** — draw an `<association>` from the boundary event to a handler script task (the task that performs the undo logic). The association must be `associationDirection="One"`.

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
  <script>variables["hotelStatus"] = "reserved";</script>
</scriptTask>
<scriptTask id="book_flight" name="Book Flight" scriptFormat="csharp">
  <script>variables["flightStatus"] = "booked";</script>
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
  <script>variables["hotelStatus"] = "cancelled";</script>
</scriptTask>
<scriptTask id="cancel_flight" name="Cancel Flight" scriptFormat="csharp">
  <script>variables["flightStatus"] = "cancelled";</script>
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

Open `https://localhost:7124/instances/{instanceId}` and switch to the **Compensation** tab to see, per workflow instance:

- **Sequence**, **Compensable Activity**, **Handler**, **Scope** (or `(root)` if attached at the root scope), **Variables** (count summary; click for full key/value dialog), and a **Status** badge: `Accent="Compensated"` for handlers that have already run, `Neutral="Pending"` for entries the engine knows about but hasn't compensated yet.
- Rows are ordered **newest-first** by completion sequence. After a broadcast-throw walk over the example above, the top row is `cancel_flight` and the second is `cancel_hotel`.
- The tab is always rendered; an empty-state message appears when no compensable activities have completed on the instance yet. There is no auto-refresh — re-render the page to take a fresh snapshot.

The tab fetches via the `ICompensationLogService` application service which activates the workflow grain on every call. It is intended for admin-UI inspection only — list views and analytics endpoints continue reading from the EF projection.

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

**Constraint:** **Multi-instance transactions** are rejected at parse time (`BpmnConverter.cs:578-581` — typed exception). This is a deliberate restriction, not a bug — multi-instance + atomicity has subtle interactions with compensation walk ordering that #307 will address.

**Nested transactions:** parse and run on the happy path. **Do not place a Cancel End Event inside an inner nested transaction** — cancel-path semantics for nested transactions land in later phases of [#307](https://github.com/nightBaker/fleans/issues/307).
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
  <exclusiveGateway id="gw" />
  <cancelEndEvent id="cancel_end" name="Payment Rejected" />
  <endEvent id="tx_end" name="Payment Accepted" />

  <sequenceFlow sourceRef="tx_start" targetRef="reserve" />
  <sequenceFlow sourceRef="reserve" targetRef="gw" />
  <sequenceFlow sourceRef="gw" targetRef="cancel_end">
    <conditionExpression>= rejected == true</conditionExpression>
  </sequenceFlow>
  <sequenceFlow sourceRef="gw" targetRef="tx_end" />
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

<sequenceFlow sourceRef="cancel_boundary" targetRef="notify_rejected" />
<sequenceFlow sourceRef="notify_rejected" targetRef="end" />
```

When the workflow reaches `cancel_end`:
1. `release_reserve` (compensation handler for `reserve`) runs
2. `cancel_boundary` fires
3. `notify_rejected` executes
4. Process ends

### Notes

- A Cancel End Event is only valid inside a `<transaction>`. Using it elsewhere has no defined BPMN behavior.
- A Cancel Boundary Event (`cancelActivity="true"`) is always interrupting — it always cancels the transaction scope.
- The transaction outcome is recorded as `Cancelled` in the engine state (queryable via the grain interface) and can be used for audit or downstream decisions.
- Compensation handlers inside the transaction run in the same isolated-scope model as standalone compensation events — each handler gets a fresh child variable scope seeded with the compensable activity's completion-time snapshot.
