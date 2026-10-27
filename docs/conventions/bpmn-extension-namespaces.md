# BPMN extension namespace policy

The parser accepts three URIs for the engine's extension elements (`taskDefinition`, `ioMapping > input/output`, `subscription`, `expectedOutputs`, multi-instance loop attrs, `correlationKey`):

| URI | Prefix | Status |
|---|---|---|
| `https://fleans.io/schema/bpmn/1.0` | `fleans:` | **Current.** The editor writes new BPMN here. |
| `http://fleans.io/schema/bpmn/fleans` | `fleans:` | **Legacy.** Kept for back-compat — see `BpmnNamespaces.FleansLegacy`. Remove once in-the-wild files are gone. |
| `http://camunda.org/schema/zeebe/1.0` | `zeebe:` | Kept indefinitely so files exported from Camunda's modeler still deploy. |

All parser sites probe namespaces via `BpmnNamespaces.FindExtensionElement` / `GetExtensionAttributeValue` (probe order: `Fleans`, `FleansLegacy`, `Zeebe`).

## `<fleans:expectedOutputs>` shape

Inside `<fleans:expectedOutputs>` the child element is `<fleans:expectedOutput name="…">`. The legacy `<fleans:output name="…">` shape is still parsed but **no longer written**.

## `<fleans:ioMapping>` on every element (#948)

`ioMapping > input/output` is read for **every** element by one helper, `IoMappingParser` (called from `BpmnConverter.ApplyIoMappings` at the end of each `ParseActivities` scope — not per element loop), and stored on the `Activity` base record (`InputMappings` / `OutputMappings`; a multi-instance wrapper's *inner* activity). The accepted elements mirror Camunda 8's `zeebe:ioMapping`: inputs on tasks, sub-processes, transactions, call activities, intermediate throw and end events; outputs on tasks, sub-processes, transactions, call activities, intermediate catch, boundary and start events. Anything else (gateways, event sub-processes) is a deploy-time error. `zeebe:ioMapping` takes the same path.

Sources are C# (DynamicExpresso) after the `=`; variables are top-level identifiers, the scope is also `_context`. `DynamicExpressoMappingExpressionEvaluator` walks plain paths (`=a.b.c`, missing → `null`) without compiling, so the old path grammar keeps its semantics. The parser checks only shape (target identifier, non-empty source) — expressions are validated when they run.

Evaluation is the engine's, never a plugin's: `WorkflowExecution.MapInputs` (called by the grain's execution loop before an activity executes) opens the input scope (`ActivityInputScopeOpened`); the completion funnels (`MarkCompleted`, `CompleteActivityInternal`, sub-process scope completion) filter the result through `MapOutputs`. An expression error becomes `FailActivity`. User-facing semantics: `website/src/content/docs/concepts/activities/io-mapping.md`.

A call activity's own `<inputMapping>` / `<outputMapping>` (child workflow ⇄ parent) are a separate mechanism, stored as `CallActivity.ChildInputMappings` / `ChildOutputMappings`.
