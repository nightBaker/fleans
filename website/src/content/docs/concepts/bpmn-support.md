---
title: BPMN Support
description: Which BPMN elements Fleans currently supports.
---

Fleans implements a growing subset of BPMN 2.0. See the project `README.md` for the authoritative,
up-to-date coverage matrix. Highlights:

- **Tasks**: Script Task, Service Task, User Task, Call Activity
- **Gateways**: Exclusive, Parallel, Inclusive, Complex (fork with conditional outgoing flows; join with optional `activationCondition`), Event-Based
- **Events**: Start, End, Intermediate Timer, Intermediate Message, Intermediate Signal
- **Boundary Events**: Timer, Message, Signal (interrupting and non-interrupting), Error (always interrupting per BPMN spec). Error boundaries can specify a specific error code to catch, or leave it empty to catch any error raised by the attached activity. In the BPMN editor's properties panel, selecting an error boundary exposes an **Error Code** field (maps to `errorRef` → `<bpmn:error errorCode="…"/>`); the _Interrupting_ checkbox is disabled for error boundaries because the spec mandates interrupting behaviour.
- **Multi-Instance**: Any task or sub-process can be configured as multi-instance (parallel or sequential) via the properties panel. Enable the **Multi-Instance** checkbox, then set **Loop Cardinality** (fixed instance count) or **Input Collection** / **Input Data Item** (iterate over a workflow variable). Output collection and output data item control how results are gathered. Collection attributes use the Zeebe namespace (`zeebe:collection`, `zeebe:elementVariable`, etc.) for Camunda compatibility.
- **Subprocesses**: Embedded, Call Activity
- **Transaction Sub-Process** (`<transaction>`): A special subprocess with atomicity semantics and three possible terminal outcomes: **Completed** (normal exit), **Cancelled** (Cancel End Event fires — requires #230), and **Hazard** (unhandled error escapes the scope — requires #231). Phase 1 (this release) supports the Completed path: the transaction scope executes like a regular subprocess, variables merge into the parent scope on exit, and the outcome is recorded keyed by the transaction activity instance id. Nested transactions and multi-instance transactions are rejected at parse time. Cancel and Compensation paths ship in a follow-up once Cancel End Event and Compensation Event are implemented.
- **Event Sub-Processes**: Error-, timer-, message-, and signal-triggered (`<subProcess triggeredByEvent="true">`), both **interrupting and non-interrupting** variants. Interrupting variants cancel enclosing-scope siblings on fire and wind the workflow down through the handler. Non-interrupting variants run the handler in parallel with the parent flow, seed the handler's isolated child variable scope with a snapshot of the enclosing scope's variables, and leave other listeners armed; timer cycles re-register automatically, and message/signal listeners re-subscribe so subsequent deliveries reach the scope. Error event sub-processes are always interrupting per BPMN 2.0 §10.2.4. A `BoundaryErrorEvent` on the failing activity takes precedence over an error event sub-process. Message correlation keys are resolved at scope entry against the enclosing scope's variables, so the correlation variable must be populated before the scope starts.
- **Compensation Events**: Compensation boundary events, intermediate throw events (broadcast and targeted), and compensation end events. See [Compensation Events](#compensation-events) below.

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
