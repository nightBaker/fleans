# Multi-Instance Activity — Design

**Date:** 2026-02-27
**Replaces:** `2026-02-26-multi-instance-parallel-design.md` (different architecture)
**Status:** Design approved

## Goal

Allow any activity to execute N times — in parallel or sequentially — each with its own variable scope, with optional output aggregation. Implements the BPMN `multiInstanceLoopCharacteristics` element.

## Scope

**In scope:**
- Parallel multi-instance (`isSequential=false`) — all iterations run concurrently
- Sequential multi-instance (`isSequential=true`) — iterations run one after another
- Collection-based iteration (`inputCollection` variable reference + `inputDataItem`)
- Cardinality-based iteration (`loopCardinality` fixed count)
- Output aggregation (`outputDataItem` + `outputCollection`)
- Per-iteration variable isolation and cleanup after completion
- Any activity type can be multi-instance
- BPMN XML parsing (standard + Zeebe/Camunda extension attributes)
- Manual test fixtures

**Out of scope:**
- `completionCondition` (early exit) — all instances must complete
- `loopCardinality` as expression (only integer literal supported)

## Architecture: Wrapper Activity

Multi-instance is implemented as a **wrapper activity** (`MultiInstanceActivity`) rather than a property on the `Activity` base class. This keeps the base class clean and follows the single-responsibility principle.

When `BpmnConverter` parses an activity with `<multiInstanceLoopCharacteristics>`, it wraps the parsed activity in `MultiInstanceActivity`, which holds the inner activity reference and loop configuration. The wrapper shares the same `ActivityId` as the inner activity for sequence flow resolution.

## Domain Model

### MultiInstanceActivity

New record in `Fleans.Domain/Activities/`:

```csharp
[GenerateSerializer]
public record MultiInstanceActivity(
    [property: Id(0)] string ActivityId,
    [property: Id(1)] Activity InnerActivity,
    [property: Id(2)] bool IsSequential,
    [property: Id(3)] int? LoopCardinality,
    [property: Id(4)] string? InputCollection,
    [property: Id(5)] string? InputDataItem,
    [property: Id(6)] string? OutputCollection,
    [property: Id(7)] string? OutputDataItem
) : Activity(ActivityId);
```

### ActivityInstanceEntry changes

Add iteration tracking:

```csharp
[Id(6)] public int? MultiInstanceIndex { get; private set; }  // null = host, non-null = iteration
```

### WorkflowInstanceState changes

Add cleanup method:

```csharp
public void RemoveVariableStates(IEnumerable<Guid> variableStateIds)
```

## Execution Flow

### Parallel Mode

1. **ExecuteAsync** on `MultiInstanceActivity`:
   - Marks itself as executing
   - Reads collection from variables (or uses LoopCardinality for count)
   - Returns `SpawnMultiInstanceIterationCommand` for each item (all N at once)

2. **ProcessCommands** handles each spawn:
   - Creates child variable scope with `elementVariable` bound to item value and `loopCounter` set to index
   - Creates `ActivityInstanceEntry` with `ScopeId = hostInstanceId` and `MultiInstanceIndex = i`
   - Creates `ActivityInstanceGrain` for the inner activity

3. **Main execution loop** picks up all spawned iterations and executes them concurrently.

4. **Scope completion** (extending `CompleteFinishedSubProcessScopes`):
   - Detects MultiInstanceActivity host where all scoped children are completed
   - Aggregates `OutputDataItem` from each child scope into `OutputCollection` on parent scope
   - Removes child variable scopes from state
   - Completes host entry, transitions to next activities

### Sequential Mode

1. **ExecuteAsync** returns only **one** `SpawnMultiInstanceIterationCommand` (index 0).

2. **Scope completion** detects the completed iteration:
   - Collects output from completed iteration
   - If more items remain: spawns next iteration (index + 1)
   - If all done: aggregates output and completes host

3. **Progress tracking**: Host entry tracks `MultiInstanceTotal` and `MultiInstanceCompleted` to know when all iterations are done and which index to spawn next.

### Failure Handling

When any iteration fails:
- Error bubbles up via `FailActivityWithBoundaryCheck`
- No boundary handler on the iteration itself (boundaries attach to the host)
- Remaining iterations cancelled via `CancelScopeChildren(hostInstanceId)`
- Host entry fails
- Host's error boundary fires if present

### Variable Cleanup

After all iterations complete (or on failure):
- All child `WorkflowVariablesState` entries created for iterations are removed from `State.VariableStates`
- This prevents state bloat, especially for large collections

## BPMN Parsing

In `BpmnConverter`, after parsing any activity, check for `<multiInstanceLoopCharacteristics>`:

```xml
<!-- Cardinality-based -->
<scriptTask id="task1" scriptFormat="csharp">
  <script>_context.x = 1</script>
  <multiInstanceLoopCharacteristics isSequential="false">
    <loopCardinality>5</loopCardinality>
  </multiInstanceLoopCharacteristics>
</scriptTask>

<!-- Collection-based (Zeebe extension) -->
<scriptTask id="task2" scriptFormat="csharp">
  <script>_context.result = _context.item</script>
  <multiInstanceLoopCharacteristics isSequential="true"
    zeebe:collection="items" zeebe:elementVariable="item"
    zeebe:outputCollection="results" zeebe:outputElement="result" />
</scriptTask>
```

If present, wrap the parsed activity in `MultiInstanceActivity` with the same `ActivityId`.

## Testing

### Unit tests (Domain)
- `MultiInstanceActivity.ExecuteAsync` returns correct commands for parallel/sequential
- `MultiInstanceActivity.GetNextActivities` follows normal sequence flows

### Application-level tests
1. **Cardinality parallel:** 3 iterations, all complete, host transitions to next
2. **Collection parallel:** items=[A,B,C], each iteration gets correct item, output aggregated
3. **Sequential:** items processed one at a time, ordered output
4. **Variable cleanup:** child scopes removed after completion, no leak to parent
5. **Failure:** one iteration fails → remaining cancelled → error boundary fires
6. **Empty collection:** completes immediately, output = empty list

### BPMN parsing tests
- Parse cardinality-based multi-instance
- Parse collection-based with Zeebe attributes
- Verify wrapper wraps correct inner activity type

### Manual test fixtures
- `tests/manual/13-multi-instance/parallel-collection.bpmn`
- `tests/manual/13-multi-instance/parallel-cardinality.bpmn`
- `tests/manual/13-multi-instance/sequential-collection.bpmn`
- `tests/manual/13-multi-instance/test-plan.md`

## Files Changed

### New files
| File | Purpose |
|------|---------|
| `Fleans.Domain/Activities/MultiInstanceActivity.cs` | Wrapper activity with MI execution logic |
| `Fleans.Domain/ExecutionCommands.cs` (additions) | `SpawnMultiInstanceIterationCommand` |
| `Fleans.Application.Tests/MultiInstanceTests.cs` | Application-level tests |
| `Fleans.Infrastructure.Tests/BpmnConverter/MultiInstanceParsingTests.cs` | BPMN parsing tests |
| `tests/manual/13-multi-instance/` | Manual test fixtures |

### Modified files
| File | Change |
|------|--------|
| `Fleans.Domain/States/ActivityInstanceEntry.cs` | Add `MultiInstanceIndex` field |
| `Fleans.Domain/States/WorkflowInstanceState.cs` | Add `RemoveVariableStates()` |
| `Fleans.Application/Grains/WorkflowInstance.Execution.cs` | Handle new command, scope completion |
| `Fleans.Application/Grains/WorkflowInstance.Logging.cs` | MI log messages |
| `Fleans.Infrastructure/Bpmn/BpmnConverter.cs` | Parse `multiInstanceLoopCharacteristics` |
| `README.md` | Update BPMN coverage table |
