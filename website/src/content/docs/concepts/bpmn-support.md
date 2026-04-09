---
title: BPMN Support
description: Which BPMN elements Fleans currently supports.
---

Fleans implements a growing subset of BPMN 2.0. See the project `README.md` for the authoritative,
up-to-date coverage matrix. Highlights:

- **Tasks**: Script Task, Service Task, User Task, Call Activity
- **Gateways**: Exclusive, Parallel, Inclusive, Complex (fork with conditional outgoing flows; join with optional `activationCondition`), Event-Based
- **Events**: Start, End, Intermediate Timer, Intermediate Message, Intermediate Signal
- **Boundary Events**: Timer, Message, Signal (interrupting and non-interrupting)
- **Subprocesses**: Embedded, Call Activity
- **Event Sub-Processes**: Error-triggered, interrupting (`<subProcess triggeredByEvent="true">` with an `errorEventDefinition` start event). When an activity in the enclosing scope fails, the matching error event sub-process cancels siblings, runs its handler in an isolated child variable scope, and the workflow winds down through the handler. `BoundaryErrorEvent` on the failing activity takes precedence. Timer / message / signal triggers and the non-interrupting variant are pending.
