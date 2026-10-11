---
title: What is BPMN?
description: A brief introduction to the BPMN 2.0 standard and its core symbols.
---

**Business Process Model and Notation (BPMN)** is an internationally recognised standard
([ISO 19510](https://www.iso.org/standard/62652.html)) maintained by the
[Object Management Group (OMG)](https://www.omg.org/spec/BPMN/2.0/).
It gives business analysts and developers a shared visual language for describing
workflows — from a simple approval flow to a multi-department order-fulfilment pipeline.

Because BPMN diagrams are backed by a well-defined XML schema, a diagram is not just
documentation: it is an executable specification. Engines such as Fleans parse that XML
and run the process exactly as drawn.

## The four symbol families

Every BPMN diagram is built from just four categories of symbols.

| Family | Shape | Purpose | Examples |
|---|---|---|---|
| **Events** | Circle | Something that *happens* — starts, interrupts, or ends a process | Start Event, Timer, Message, End Event |
| **Activities** | Rounded rectangle | A unit of *work* the engine executes | Script Task, User Task, Call Activity, Sub-Process |
| **Gateways** | Diamond | A *routing decision* that splits or merges the flow | Exclusive (XOR), Parallel (AND), Inclusive (OR) |
| **Flows** | Arrow | *Connects* the above elements in sequence | Sequence Flow, Message Flow |

## A minimal example

The simplest possible BPMN process is a start event, one task, and an end event connected
by sequence flows. Add one exclusive gateway and you can already model a real approval:

<figure class="arch-diagram" style="--arch-ratio: 720 / 422">
  <iframe data-arch-src="/fleans/diagrams/bpmn-overview-flow.html" src="/fleans/diagrams/bpmn-overview-flow.html?embed=1" title="Minimal BPMN approval process" loading="lazy"></iframe>
  <figcaption>A start event, a user task, an exclusive gateway and a script task joined by sequence flows, ending in one of two end events. <a href="/fleans/diagrams/bpmn-overview-flow.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

In practice you also layer in parallel gateways, intermediate events for waiting on
timers or messages, and sub-processes for encapsulating reusable logic.

## Where to go next

- **[BPMN Support](/fleans/concepts/bpmn-support/)** — see which BPMN elements Fleans
  implements today and how to use them.
- **[OMG BPMN 2.0 Specification](https://www.omg.org/spec/BPMN/2.0/)** — the full
  standard if you want every detail.

:::note
BPMN also defines **Pools** and **Lanes** for modelling cross-organisation collaboration.
Fleans does not currently support these constructs, but the core execution semantics
(events, activities, gateways, flows) cover the vast majority of orchestration use cases.
:::
