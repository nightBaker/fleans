---
title: Input/Output Mapping
description: fleans:ioMapping on tasks, sub-processes, call activities and events — C# expressions that shape the variables an activity sees and hands back, with Camunda 8 semantics.
sidebar:
  order: 2
---

`<fleans:ioMapping>` shapes the variables an activity works with. **Inputs** compute
activity-local variables before the activity starts; **outputs** decide what the activity hands
back to the process when it completes. It follows Camunda 8's `zeebe:ioMapping`: the same
elements accept it, with the same semantics. The one difference is that expressions are C#,
not FEEL.

```xml
<!-- Requires xmlns:fleans="https://fleans.io/schema/bpmn/1.0" on <bpmn:definitions> -->
<bpmn:scriptTask id="price" scriptFormat="csharp">
  <bpmn:extensionElements>
    <fleans:ioMapping>
      <fleans:input  source="=order.Total * 1.12" target="totalWithVat" />
      <fleans:output source="=totalWithVat > 1000 ? &quot;review&quot; : &quot;auto&quot;" target="approval" />
    </fleans:ioMapping>
  </bpmn:extensionElements>
  <bpmn:script>_context.rounded = Math.Round(_context.totalWithVat, 2)</bpmn:script>
</bpmn:scriptTask>
```

Here the script sees `totalWithVat` as its own variable. After the task completes, the process
has `approval` and nothing else from it: neither `totalWithVat` nor `rounded`.

## Where it is allowed

| Element | Inputs | Outputs |
|---|:---:|:---:|
| Tasks: `task`, `userTask`, `scriptTask`, `serviceTask` (custom tasks too) | ✅ | ✅ |
| Embedded `subProcess`, `transaction` | ✅ | ✅ |
| `callActivity` | ✅ | ✅ |
| `intermediateThrowEvent`, `endEvent` | ✅ | — |
| `intermediateCatchEvent` (message, timer, signal, …), `boundaryEvent`, `startEvent` | — | ✅ |
| Gateways, event sub-processes | — | — |

A mapping on an element that does not accept it is rejected when you deploy, with the element's
id in the message.

**In the Admin UI editor**, select the element: the inspector's **Input/Output mapping** section
shows inputs and outputs only where this table allows them, and edits the element's `ioMapping`
directly. See [BPMN Editor](/fleans/guides/editor/#inputoutput-mapping).

## Sources and targets

- **`target`** is a plain identifier (`^[a-zA-Z_][a-zA-Z0-9_]*$`): the variable the mapping writes.
- **`source`** starting with `=` is a **C# expression**, evaluated by
  [DynamicExpresso](https://github.com/dynamicexpresso/DynamicExpresso), the same engine as
  gateway conditions and script tasks. Variables are top-level identifiers (`=order.Total`). The
  whole scope is also available as `_context` (`=_context.order.Total`).
- **`source`** without a leading `=` is a string literal (`source="application/json"`).

| Source | Result |
|---|---|
| `=userId`, `=order.customer.id` | The variable, or a nested field. A missing segment yields `null`. |
| `=42`, `=1.5`, `=true`, `=null`, `="GET"` | A literal. Integers are `long`, like numbers in workflow variables. |
| `=order.Total * 1.12`, `=items.Count`, `=name.ToUpper()`, `=a > b ? a : b` | Any C# expression DynamicExpresso understands. |
| `GET` | The string `GET`. |

Simple paths such as `=a.b.c` are also valid FEEL, so the `zeebe:ioMapping` blocks Camunda
exports usually deploy unchanged. `zeebe:ioMapping` is read by the same parser.

:::caution[Variables have JSON types]
Numbers in workflow variables are `long` or `double`, and nested objects are dynamic. Mixing
`double` and `decimal` (`=order.Total * 1.12m`) fails at run time when `Total` is a `double`.
Use `1.12` instead.
:::

## Semantics

**Inputs** run when the activity is reached, before it executes. Each one is evaluated against
the enclosing scope (plus the inputs before it) and written to a new **activity-local scope**.
The activity reads through that scope: a script sees the inputs as `_context.<target>`, a custom
task's plugin receives them as its resolved inputs, and a sub-process's or call activity's
children see them. Inputs stay local: once the activity completes, the process does not see
them, and the local scope is removed.

**Outputs** run when the activity completes. They are evaluated against the activity's local
variables, overlaid with its **result**:

| Element | Result |
|---|---|
| Script task | The variables the script set |
| User task | The variables passed to `complete` |
| Custom (service) task | The dictionary the plugin returned, e.g. `__response` |
| Call activity | The child's variables, after the call activity's own `outputMapping`s |
| Message catch / message boundary event | The message's variables |
| Start event, timer / signal / error / escalation event | Nothing: outputs read the enclosing scope |
| Sub-process | The sub-process's own scope |

When an activity has outputs, **only** the mapped outputs reach the enclosing scope; the rest
of the result is dropped. Without outputs the whole result merges into the enclosing scope, as
before, except the input targets, which stay local.

**Errors.** An input or output that does not evaluate (a syntax error, a member missing on a
`null`, a type mismatch) **fails the activity** with code `500`. The message names the
expression, the target and the activity, e.g.
`Input mapping '=count +' -> 'scaled' on activity 'script1' failed: …`. An error boundary event
or error event sub-process can catch it. Otherwise the instance fails.

**Multi-instance.** Mappings apply to each iteration: every iteration evaluates its inputs
against its own scope (so `=item` and `=loopCounter` work) and filters its own result before
`outputElement` is collected.

## Migrating from Camunda

- **Camunda 8** `zeebe:ioMapping` with simple paths deploys as-is. Rewrite FEEL-only
  constructs (`if … then … else`, `string join(…)`, list filters) as C#.
- **Camunda 7** `camunda:inputOutput` (including Groovy scripts) is not read. Rewrite each
  `camunda:inputParameter` / `camunda:outputParameter` as a `fleans:input` / `fleans:output`
  with a C# expression.
- A call activity's own `<inputMapping>` / `<outputMapping>` still decide what crosses into and
  out of the child workflow. `fleans:ioMapping` on the call activity works around them (see
  [Call activities and sub-processes](/fleans/guides/call-activities-and-subprocesses/)).

See also: [Variables and scope](/fleans/guides/variables-and-scope/) ·
[Custom tasks](/fleans/concepts/custom-tasks/) · [Tasks](../tasks/)
