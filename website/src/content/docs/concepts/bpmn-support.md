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
- **Event Sub-Processes**: Error-triggered and timer-triggered, interrupting (`<subProcess triggeredByEvent="true">`). Error variant uses an `errorEventDefinition` start event; timer variant uses a `timerEventDefinition` (`timeDuration` / `timeDate`; cycle timers land in a later slice). Both cancel enclosing-scope siblings on firing, run their handler in an isolated child variable scope, and wind the workflow down through the handler. A `BoundaryErrorEvent` on the failing activity takes precedence over an error event sub-process. Timer listeners are armed when their enclosing scope is entered and unregistered on scope completion or cancellation. Message / signal triggers and the non-interrupting variant are pending.
