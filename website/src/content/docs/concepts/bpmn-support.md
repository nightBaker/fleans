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
- **Event Sub-Processes**: Error-, timer-, message-, and signal-triggered (`<subProcess triggeredByEvent="true">`), both **interrupting and non-interrupting** variants. Interrupting variants cancel enclosing-scope siblings on fire and wind the workflow down through the handler. Non-interrupting variants run the handler in parallel with the parent flow, seed the handler's isolated child variable scope with a snapshot of the enclosing scope's variables, and leave other listeners armed; timer cycles re-register automatically, and message/signal listeners re-subscribe so subsequent deliveries reach the scope. Error event sub-processes are always interrupting per BPMN 2.0 §10.2.4. A `BoundaryErrorEvent` on the failing activity takes precedence over an error event sub-process. Message correlation keys are resolved at scope entry against the enclosing scope's variables, so the correlation variable must be populated before the scope starts.
