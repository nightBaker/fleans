---
title: BPMN Support
description: Which BPMN elements Fleans currently supports.
---

Fleans implements a growing subset of BPMN 2.0. See the project `README.md` for the authoritative,
up-to-date coverage matrix. Highlights:

- **Tasks**: Script Task, Service Task, User Task, Call Activity
- **Gateways**: Exclusive, Parallel
- **Events**: Start, End, Intermediate Timer, Intermediate Message, Intermediate Signal
- **Boundary Events**: Timer, Message, Signal (interrupting and non-interrupting)
- **Subprocesses**: Embedded, Call Activity
- **Event Sub-Processes**: Error-, timer-, message-, and signal-triggered (`<subProcess triggeredByEvent="true">`), both **interrupting and non-interrupting** variants. Interrupting variants cancel enclosing-scope siblings on fire and wind the workflow down through the handler. Non-interrupting variants run the handler in parallel with the parent flow, seed the handler's isolated child variable scope with a snapshot of the enclosing scope's variables, and leave other listeners armed; timer cycles re-register automatically, and message/signal listeners re-subscribe so subsequent deliveries reach the scope. Error event sub-processes are always interrupting per BPMN 2.0 §10.2.4. A `BoundaryErrorEvent` on the failing activity takes precedence over an error event sub-process. Message correlation keys are resolved at scope entry against the enclosing scope's variables, so the correlation variable must be populated before the scope starts.
