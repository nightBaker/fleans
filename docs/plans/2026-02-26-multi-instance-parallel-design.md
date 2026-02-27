# Multi-Instance Activity (Parallel) — Design

**Date:** 2026-02-26
**Phase:** 2.4 from architectural risk audit
**Status:** Design approved

## Goal

Allow any activity (ScriptTask, SubProcess, CallActivity) to execute N times in parallel, each with its own variable scope, with optional output aggregation. This is the BPMN `multiInstanceLoopCharacteristics` with `isSequential=false`.

## Scope

**In scope:**
- Collection-based iteration (`inputCollection` + `inputDataItem`)
- Cardinality-based iteration (`loopCardinality`)
- Output aggregation (`outputDataItem` + `outputCollection`)
- Failure handling (any iteration fails → cancel remaining → fail host)
- BPMN XML parsing (standard + Camunda extension attributes)
- Manual test fixtures

**Out of scope (deferred to 2.5):**
- Sequential multi-instance (`isSequential=true`)
- `completionCondition` (early exit)
- Multi-instance on TaskActivity (requires instance-level completion API)

## Domain Model

### MultiInstanceLoopCharacteristics

New record in `Fleans.Domain/Activities/`:

```csharp
[GenerateSerializer]
public record MultiInstanceLoopCharacteristics(
    [property: Id(0)] bool IsSequential,
    [property: Id(1)] int? LoopCardinality,
    [property: Id(2)] string? InputCollection,
    [property: Id(3)] string? InputDataItem,
    [property: Id(4)] string? OutputCollection,
    [property: Id(5)] string? OutputDataItem
);
```

### Activity base class change

Add nullable property to `Activity`:

```csharp
[Id(1)]
public MultiInstanceLoopCharacteristics? LoopCharacteristics { get; init; }
```

### ActivityInstanceEntry change

Add iteration index to distinguish host entries from iteration entries:

```csharp
[Id(6)]
public int? MultiInstanceIndex { get; private set; }
```

- `null` = normal entry or multi-instance host
- Non-null = iteration entry at that index

Constructor overload or setter method for setting the index.

## Execution Flow

### OpenMultiInstanceScope()

New method on `WorkflowInstance`, called when an activity with `LoopCharacteristics` (parallel) begins executing:

1. **Resolve iteration count N:**
   - If `LoopCardinality` set: N = that value
   - If `InputCollection` set: resolve variable from scope, N = collection length
   - Validation: must have one or the other; N > 0

2. **Validate activity type:** Must be ScriptTask, SubProcess, or CallActivity. TaskActivity is not supported for multi-instance (requires instance-level completion API not yet implemented).

3. **For each iteration i (0..N-1):**
   - Create child variable scope via `State.AddChildVariableState(parentVariablesId)`
   - Set `loopCounter = i` in child scope
   - If collection-driven: set `InputDataItem = collection[i]` in child scope
   - Create `ActivityInstanceEntry` with:
     - Same `ActivityId` as host
     - `ScopeId = hostActivityInstanceId`
     - `MultiInstanceIndex = i`

4. Host entry stays active as scope parent (like SubProcess).

### Execution loop interaction

In `ExecuteWorkflow()`, when the loop picks up an activity entry:
- If `entry.MultiInstanceIndex != null`: execute the activity normally (skip `LoopCharacteristics` check). The iteration is just a regular execution of the inner activity.
- If `entry.MultiInstanceIndex == null` and activity has `LoopCharacteristics`: call `OpenMultiInstanceScope()` instead of `ExecuteAsync()`.

### Scope completion

Extend `CompleteFinishedSubProcessScopes()` to handle multi-instance hosts in addition to SubProcess:

- Check: activity has `LoopCharacteristics` AND is not itself an iteration entry
- All scoped children (`ScopeId == hostInstanceId`) completed?
- If yes:
  1. **Aggregate outputs** (if configured): collect `OutputDataItem` from each child scope, ordered by `MultiInstanceIndex`, into a list. Set as `OutputCollection` on parent scope.
  2. **Clean up child variable scopes:** remove all child `WorkflowVariablesState` entries from `State.VariableStates`.
  3. Complete host entry, transition to next activities.

### Failure handling

When any iteration fails via `FailActivityWithBoundaryCheck`:
- The iteration entry is in a scope under the host
- No boundary handler found on the iteration (boundary events attach to the host, not iterations)
- Error bubbles up: cancel remaining iteration siblings via `CancelScopeChildren(hostInstanceId)`
- Fail the host entry
- Host's error boundary fires if present

## BPMN Parsing

In `BpmnConverter.ParseActivities()`, after creating any activity, check for child element:

```xml
<bpmn:multiInstanceLoopCharacteristics isSequential="false">
  <bpmn:loopCardinality>3</bpmn:loopCardinality>
</bpmn:multiInstanceLoopCharacteristics>
```

Or Camunda-style collection:
```xml
<bpmn:multiInstanceLoopCharacteristics isSequential="false"
  camunda:collection="orders" camunda:elementVariable="order" />
```

Parse into `MultiInstanceLoopCharacteristics` and set on the activity's `LoopCharacteristics` property.

## Testing

### Application-level tests (MultiInstanceTests.cs)

1. **Cardinality-based parallel:** ScriptTask with `LoopCardinality=3`. Each script sets a result variable. Verify 3 iterations created, all complete, host transitions to next activity.

2. **Collection-based parallel:** ScriptTask with `InputCollection="items"`, `InputDataItem="item"`. Set `items=[A,B,C]`. Verify each iteration received correct item.

3. **Output aggregation:** ScriptTask with output mapping. Verify parent scope gets `OutputCollection` list ordered by iteration index.

4. **Variable isolation + cleanup:** Verify child scope variables don't leak to parent. Verify child scopes removed from `State.VariableStates` after completion.

5. **Multi-instance on SubProcess:** SubProcess with multi-instance. Verify each iteration creates nested scope.

6. **Failure in one iteration:** One iteration fails → remaining cancelled → host fails → error boundary fires.

### Manual test plan

**Folder:** `tests/manual/13-multi-instance/`

**Fixture 1: `parallel-collection.bpmn`**
- Process: `start → setItems (ScriptTask sets items=["A","B","C"]) → reviewTasks (ScriptTask, multi-instance parallel, inputCollection="items", inputDataItem="item", outputDataItem="result", outputCollection="results", script: _context.result = "reviewed-" + _context.item) → end`

**Fixture 2: `parallel-cardinality.bpmn`**
- Process: `start → repeatTask (ScriptTask, multi-instance parallel, loopCardinality=3, script: _context.result = "iter-" + _context.loopCounter) → end`

**test-plan.md:**

### Scenario 13a: Collection-based parallel

1. Deploy `parallel-collection.bpmn`
2. Start an instance
3. Verify:
   - [ ] Instance status: Completed
   - [ ] Completed activities: start, setItems, reviewTasks (3 iterations), end
   - [ ] Variables: `results=["reviewed-A","reviewed-B","reviewed-C"]`
   - [ ] No error activities

### Scenario 13b: Cardinality-based parallel

1. Deploy `parallel-cardinality.bpmn`
2. Start an instance
3. Verify:
   - [ ] Instance status: Completed
   - [ ] Completed activities: start, repeatTask (3 iterations), end
   - [ ] No error activities
