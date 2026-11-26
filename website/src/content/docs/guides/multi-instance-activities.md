---
title: Multi-Instance Activities
description: Fan out work in parallel or run iterations sequentially using BPMN multi-instance loops, with worked parallel-cardinality, parallel-collection, and sequential-collection examples.
---


A **multi-instance activity** runs the same activity body multiple times — either concurrently (*parallel*) or one at a time (*sequential*). It's BPMN's answer to fan-out: "send a notification to every approver", "process each line in this CSV", "spin up a sub-process per work item". Fleans implements multi-instance for tasks, embedded subprocesses and call activities.

This guide is the developer's tour, anchored to the runnable fixtures in [`tests/manual/13-multi-instance/`](https://github.com/nightBaker/fleans/tree/main/tests/manual/13-multi-instance). For the underlying BPMN parsing rules, the canonical reference is the [BPMN Support](/fleans/concepts/bpmn-support/) page.

## When to use multi-instance

| Situation | Pick this |
| --- | --- |
| Run a task exactly *N* times, where *N* is a fixed compile-time number. | Parallel + `<loopCardinality>` |
| Iterate over a workflow-variable list/array; iterations are independent. | Parallel + `fleans:collection` |
| Iterate over a list, but iteration *N+1* must observe iteration *N*'s side effects on the enclosing scope. | Sequential + `fleans:collection` |

Two short rules of thumb:

- Reach for **parallel** when iterations are independent and you want them to start at the same time. The host activity completes when **every** iteration finishes, unless a [completion condition](#completion-condition) ends it early.
- Reach for **sequential** when each iteration depends on prior iterations' merged-back state, or when the inner activity calls a rate-limited external system.

## The four BPMN attributes you actually need

Multi-instance configuration lives inside `<bpmn:multiInstanceLoopCharacteristics>` nested in the activity element. Fleans honours four attributes:

- `isSequential="true|false"` — defaults to `false` (parallel).
- **One of**: `<loopCardinality>N</loopCardinality>` (static count) **or** `fleans:collection="varName"` (iterate over a workflow variable that resolves to a `List`/array).
- `fleans:elementVariable="..."` — names the per-iteration item variable inside the iteration's child scope. Only meaningful with `fleans:collection`.
- `fleans:outputCollection="..."` + `fleans:outputElement="..."` — names the aggregated output array on the **enclosing** scope and the per-iteration value to harvest from each child scope on completion.

Both bare-name and `fleans:`-prefixed attributes are accepted by the parser (`fleans:collection` ↔ `collection`, `fleans:elementVariable` ↔ `elementVariable`, `fleans:outputCollection` ↔ `outputCollection`, `fleans:outputElement` ↔ `outputElement`). The editor and the canonical examples in this guide use the `fleans:` prefix; files exported from Camunda's modeler may use `zeebe:` and parse via the same back-compat probe.

The `MultiInstanceActivity` constructor enforces that exactly one of `LoopCardinality` or `InputCollection` is set, and that cardinality is non-negative. Violations throw at deploy time, not at runtime.

## Parallel multi-instance over a collection

Citation: [`tests/manual/13-multi-instance/parallel-collection.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/13-multi-instance/parallel-collection.bpmn).

```xml
<!-- Requires xmlns:fleans="https://fleans.io/schema/bpmn/1.0" on <bpmn:definitions> -->
<scriptTask id="processItem" scriptFormat="csharp">
  <script>_context.result = "processed-" + _context.item</script>
  <multiInstanceLoopCharacteristics isSequential="false"
    fleans:collection="items" fleans:elementVariable="item"
    fleans:outputCollection="results" fleans:outputElement="result" />
</scriptTask>
```

An upstream script task seeds `_context.items = new List<object> { "A", "B", "C" }`. When the workflow reaches `processItem`, three iterations spawn **concurrently**, each in its own child scope:

- Iteration 0's child scope has `_context.item = "A"` and `_context.loopCounter = 0`.
- Iteration 1's child scope has `_context.item = "B"` and `_context.loopCounter = 1`.
- Iteration 2's child scope has `_context.item = "C"` and `_context.loopCounter = 2`.

Each iteration computes `_context.result = "processed-" + _context.item`. After **all three** complete, the engine appends each child scope's `result` value to a list named `results` on the **enclosing** scope, ordered by iteration index (not completion order). The outgoing sequence flow fires once with `_context.results = ["processed-A", "processed-B", "processed-C"]`.

The aggregation step lives in `MultiInstanceCoordinator.AggregateOutputVariables`.

## Parallel multi-instance with fixed cardinality

Citation: [`tests/manual/13-multi-instance/parallel-cardinality.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/13-multi-instance/parallel-cardinality.bpmn).

```xml
<scriptTask id="repeatTask" scriptFormat="csharp">
  <script>_context.result = "iter-" + _context.loopCounter</script>
  <multiInstanceLoopCharacteristics isSequential="false"
    outputCollection="results" outputElement="result">
    <loopCardinality>3</loopCardinality>
  </multiInstanceLoopCharacteristics>
</scriptTask>
```

With `<loopCardinality>3</loopCardinality>` and no `fleans:collection`, three iterations spawn but only `loopCounter` is bound on each child scope — there is no per-iteration item variable. The script body uses `_context.loopCounter` directly to differentiate iterations. After completion `_context.results == ["iter-0", "iter-1", "iter-2"]`.

This is the right pattern when you need exactly *N* concurrent runs of the same logic and the iterations can be distinguished by index alone (e.g. quorum reads, fanout to *N* identical workers).

## Sequential multi-instance

Citation: [`tests/manual/13-multi-instance/sequential-collection.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/13-multi-instance/sequential-collection.bpmn).

```xml
<!-- Requires xmlns:fleans="https://fleans.io/schema/bpmn/1.0" on <bpmn:definitions> -->
<scriptTask id="processItem" scriptFormat="csharp">
  <script>_context.result = "seq-" + _context.item</script>
  <multiInstanceLoopCharacteristics isSequential="true"
    fleans:collection="items" fleans:elementVariable="item"
    fleans:outputCollection="results" fleans:outputElement="result" />
</scriptTask>
```

The only change from the parallel-collection fixture is `isSequential="true"`. Iteration 0 runs to completion before iteration 1 spawns, and so on. `MultiInstanceCoordinator.SpawnNextSequentialIteration` is the engine hook that drives this — it fires on each iteration completion and emits the next `SpawnActivityCommand` until the collection is exhausted.

Because each iteration's `outputElement` is appended to the enclosing scope's `outputCollection` on completion (before the next iteration spawns), iteration *N+1* can read iteration *N*'s aggregated state. This is the property that makes sequential the right call for "build up a running total" or "stop calling the API once we've seen 5 successes" patterns; to stop early, add a [completion condition](#completion-condition).

## The implicit `loopCounter` variable

Every iteration's child scope is seeded with `_context.loopCounter` set to the **0-based** iteration index, regardless of cardinality vs. collection mode. Scripts and mappings inside the iteration read it like any other variable.

The three BPMN-spec aggregate counters — `nrOfInstances`, `nrOfActiveInstances`, `nrOfCompletedInstances` — are **not** workflow variables. They exist only while the [completion condition](#completion-condition) is evaluated; scripts, gateway conditions and mappings cannot read them.

## Loop-variable scope

Multi-instance leans on the same scope-tree machinery covered in [Variables and Scope](/fleans/guides/variables-and-scope/) — start there for the full mental model. The multi-instance specifics:

<figure class="arch-diagram" style="--arch-ratio: 720 / 546">
  <iframe data-arch-src="/fleans/diagrams/multi-instance-execution.html" src="/fleans/diagrams/multi-instance-execution.html?embed=1" title="Multi-instance execution flow" loading="lazy"></iframe>
  <figcaption>The host resolves its count, spawns iterations (all at once in parallel, one after another in sequential), each in its own child scope, and collects each <code>outputElement</code> into <code>outputCollection</code>; an iteration that throws fails the host. <a href="/fleans/diagrams/multi-instance-execution.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

- Each iteration spawns into a **fresh child variable scope** (`ChildVariableScopeCreated` event), seeded by inheritance from the enclosing scope.
- `loopCounter` plus (when applicable) `elementVariable` are written onto that child scope before the iteration body runs.
- On iteration completion, if `outputElement` + `outputCollection` are configured, the iteration's `outputElement` value is appended to the enclosing scope's `outputCollection` array.
- Concurrent parallel iterations have **independent** scopes — there is no last-writer-wins on `_context.item` mid-flight, because no two iterations write to the same scope.

This is the same isolation that compensation handlers rely on (each handler runs in its own child scope — see [Error Handling](/fleans/guides/error-handling/#compensation)). Treat each iteration the way you would treat a compensation handler: it owns its scope, and the only contract back to the parent is the named `outputElement`.

## What multi-instance can wrap

The parser (`BpmnConverter.TryWrapMultiInstance`) accepts `<multiInstanceLoopCharacteristics>` on:

- `<bpmn:task>`
- `<bpmn:userTask>`
- `<bpmn:serviceTask>`
- `<bpmn:scriptTask>`
- `<bpmn:subProcess>` — embedded subprocess
- `<bpmn:callActivity>` — each iteration starts its own child instance

There is one hard exclusion. **Transactions reject multi-instance at parse time.** The converter throws:

> Transaction Sub-Process '`<id>`' does not support multi-instance loop characteristics. Remove the multiInstanceLoopCharacteristics element, or use a regular Sub-Process.

If you need fan-out with transactional semantics, wrap individual transactions in an outer multi-instance subprocess — but a `<transaction>` element itself cannot carry `<multiInstanceLoopCharacteristics>`.

Event sub-processes also do not support multi-instance — by BPMN spec, not a Fleans limitation. The converter ignores `<multiInstanceLoopCharacteristics>` on an event sub-process.

## Completion condition

`<bpmn:completionCondition>` is fully supported. It lets you finish a multi-instance activity early — before all spawned iterations complete:

```xml
<!-- Requires xmlns:fleans="https://fleans.io/schema/bpmn/1.0" on <bpmn:definitions> -->
<bpmn:multiInstanceLoopCharacteristics fleans:collection="approvers" fleans:elementVariable="approver">
  <bpmn:completionCondition>_context.nrOfCompletedInstances >= 1</bpmn:completionCondition>
</bpmn:multiInstanceLoopCharacteristics>
```

**Semantics:** after each iteration completes, Fleans evaluates the condition against the current aggregate counts (`nrOfInstances`, `nrOfActiveInstances`, `nrOfCompletedInstances`). If the condition returns `true`, all remaining active iterations are cancelled and the multi-instance host completes with the outputs aggregated from the iterations that finished.

**Expression syntax:** write `_context.<counter>` explicitly — unlike gateway conditions, bare names and `${…}` are **not** rewritten here. Only the three counters are visible; workflow variables are not. A condition that throws is logged and treated as `false`, so the host waits for every iteration:
- `_context.nrOfCompletedInstances >= 1` — 1-of-N approval
- `_context.nrOfCompletedInstances >= 2` — majority (2-of-3)
- `_context.nrOfActiveInstances == 0` — equivalent to "wait for all" (same as no condition)

**Output collection:** only outputs from *completed* iterations are included in `outputCollection`. Cancelled iterations contribute nothing.

**Editor:** the BPMN editor exposes a **Completion Condition** field in the Multi-Instance section of the properties panel, so you can view and edit the expression without touching the XML directly.

## Limitations

- **Transactions reject multi-instance at parse time.** `<transaction>` elements cannot carry `<multiInstanceLoopCharacteristics>` — the converter throws an explicit error. Wrap individual transactions in an outer multi-instance subprocess instead.
- **Event sub-processes cannot be multi-instance** — by BPMN spec, not a Fleans limitation.
- **Thousands of waiting parallel branches in one instance.** Every branch that waits for a message or signal persists before it subscribes, all inside the call that reached the fan-out. On a laptop-sized stack, 1 000 branches took about 7 s and 3 000 about 26 s; from about 5 000 the call passes its 30 s timeout. `POST /Execution/start` then answers 500 even though the instance keeps running, so don't retry it blindly. Delivering a message also gets slower as an instance holds more branches. For fan-outs this large, start one instance per item instead.

## Related guides

- [Variables and Scope](/fleans/guides/variables-and-scope/) — full model for child-scope inheritance and merge-back.
- [BPMN Support](/fleans/concepts/bpmn-support/) — the canonical reference for which BPMN elements parse.
- [Error Handling](/fleans/guides/error-handling/) — how iteration failures propagate (an iteration throws → the host multi-instance activity fails → boundary events on the host can catch).
