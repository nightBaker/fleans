---
title: BPMN Support
description: Which BPMN elements Fleans currently supports.
---

Fleans implements a growing subset of BPMN 2.0. See the project `README.md` for the authoritative,
up-to-date coverage matrix. Highlights:

- **Tasks**: Script Task, Service Task, User Task, Call Activity
- **Gateways**: Exclusive, Parallel, Inclusive, Complex (fork with conditional outgoing flows; join with optional `activationCondition`), Event-Based
- **Events**: Start, End, Intermediate Timer, Intermediate Message, Intermediate Signal, Intermediate Conditional
- **Boundary Events**: Timer, Message, Signal, Conditional (interrupting and non-interrupting), Error (always interrupting per BPMN spec). Error boundaries can specify a specific error code to catch, or leave it empty to catch any error raised by the attached activity. In the BPMN editor's properties panel, selecting an error boundary exposes an **Error Code** field (maps to `errorRef` → `<bpmn:error errorCode="…"/>`); the _Interrupting_ checkbox is disabled for error boundaries because the spec mandates interrupting behaviour.
- **Subprocesses**: Embedded, Call Activity
- **Conditional Events**: Start events, intermediate catch events, and boundary events (interrupting and non-interrupting). See [Conditional Events](#conditional-events) below.
- **Event Sub-Processes**: Error-, timer-, message-, and signal-triggered (`<subProcess triggeredByEvent="true">`), both **interrupting and non-interrupting** variants. Interrupting variants cancel enclosing-scope siblings on fire and wind the workflow down through the handler. Non-interrupting variants run the handler in parallel with the parent flow, seed the handler's isolated child variable scope with a snapshot of the enclosing scope's variables, and leave other listeners armed; timer cycles re-register automatically, and message/signal listeners re-subscribe so subsequent deliveries reach the scope. Error event sub-processes are always interrupting per BPMN 2.0 §10.2.4. A `BoundaryErrorEvent` on the failing activity takes precedence over an error event sub-process. Message correlation keys are resolved at scope entry against the enclosing scope's variables, so the correlation variable must be populated before the scope starts.

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
