# Call Activity & Boundary Error Events Design

## Scope

Implement BPMN **Call Activity** — an activity that invokes an external process definition by key. Runs as a separate `WorkflowInstance` grain. Input/output variable mappings define the contract. Also implement **Boundary Error Events** to catch child workflow failures and route to recovery paths.

Embedded Sub-Process is out of scope (separate future work).

## Design Decisions

- **Execution model:** Approach B — grain-level orchestration. Parent `WorkflowInstance` creates child grain directly via `IWorkflowExecutionContext.StartChildWorkflow()`. No domain events for spawning.
- **Version resolution:** Always resolves to latest deployed version of the called process. No version pinning.
- **Variable passing:** Configurable via `PropagateAllParentVariables` and `PropagateAllChildVariables` flags (both default `true`, Camunda-compatible). Explicit input/output mappings can rename variables. When propagation flags are `false`, only mapped variables cross the boundary.
- **Error handling:** Child failure auto-fails parent Call Activity. Boundary error events can catch failures and route to alternate paths.

## Domain Model

### New Types

**`CallActivity`** (`Fleans.Domain/Activities/CallActivity.cs`):

```csharp
[GenerateSerializer]
public record CallActivity(
    string ActivityId,
    string CalledProcessKey,
    List<VariableMapping> InputMappings,
    List<VariableMapping> OutputMappings,
    bool PropagateAllParentVariables = true,
    bool PropagateAllChildVariables = true) : Activity(ActivityId)
```

- `ExecuteAsync()` calls `workflowContext.StartChildWorkflow(this, activityContext)`
- `GetNextActivities()` returns single target from outgoing sequence flow (same as TaskActivity)

**Variable propagation flags** (follows [Camunda semantics](https://docs.camunda.io/docs/components/modeler/bpmn/call-activities/)):

| Flag | Default | Behavior |
|---|---|---|
| `PropagateAllParentVariables` | `true` | All parent scope variables copied to child on start. Input mappings still applied (can rename). If `false`, only explicitly input-mapped variables reach the child. |
| `PropagateAllChildVariables` | `true` | All child variables merged back to parent on completion. Output mappings still applied (can rename). If `false`, only explicitly output-mapped variables return to parent. |

**Best practice:** Set both to `false` when the Call Activity is inside a parallel flow to prevent accidental variable overwrites.

**`VariableMapping`** (`Fleans.Domain/VariableMapping.cs`):

```csharp
[GenerateSerializer]
public record VariableMapping(string Source, string Target);
```

**`BoundaryErrorEvent`** (`Fleans.Domain/Activities/BoundaryErrorEvent.cs`):

```csharp
[GenerateSerializer]
public record BoundaryErrorEvent(
    string ActivityId,
    string AttachedToActivityId,
    string? ErrorCode) : Activity(ActivityId)
```

- Never added to active entries until a failure triggers it
- Completes immediately when activated, routes to outgoing sequence flow

### State Changes

**`WorkflowInstanceState`** — new fields:

- `Guid? ParentWorkflowInstanceId` — null for top-level workflows
- `string? ParentActivityId` — which Call Activity spawned this child

**`ActivityInstanceEntry`** — new field:

- `Guid? ChildWorkflowInstanceId` — set when this is a Call Activity that spawned a child

Output mappings are NOT stored in state — the parent looks them up from its `CallActivity` definition when the child completes.

## Execution Flow

### Spawning the Child

1. `CallActivity.ExecuteAsync()` calls `workflowContext.StartChildWorkflow(this, activityContext)`
2. `WorkflowInstance.StartChildWorkflow()`:
   - Resolves latest process definition via `WorkflowInstanceFactoryGrain.GetLatestByKey(calledProcessKey)`
   - Creates new `WorkflowInstance` grain with `Guid.NewGuid()`
   - Sets child state: `ParentWorkflowInstanceId`, `ParentActivityId`
   - Maps input variables: if `PropagateAllParentVariables`, copies all parent variables then applies `InputMappings` (renames); if `false`, applies only `InputMappings`
   - Calls `child.SetWorkflow(definition)` then `child.StartWorkflow()`
   - Stores `ChildWorkflowInstanceId` on the parent's `ActivityInstanceEntry`
   - Call Activity stays in "executing" state — workflow loop stops advancing it

### Child Completion Callback

3. Child reaches `EndEvent` → calls `this.Complete()`
4. `WorkflowInstance.Complete()` checks `ParentWorkflowInstanceId`:
   - If null → top-level, done (existing behavior)
   - If set → collects child's final variables, applies output logic: if `PropagateAllChildVariables`, sends all child variables then applies `OutputMappings` (renames); if `false`, applies only `OutputMappings`. Calls `parent.OnChildWorkflowCompleted(parentActivityId, outputVariables)` via domain event

### Child Failure Propagation

5. If child fails → calls `parent.FailActivity(parentActivityId, exception)`
6. Parent checks for `BoundaryErrorEvent` attached to the Call Activity:
   - Match found → boundary event activates, routes to recovery path
   - No match → normal failure propagation

## Boundary Error Events

### Routing Logic

In `WorkflowInstance.FailActivity()`:

1. Look up `BoundaryErrorEvent` where `AttachedToActivityId == activityId`
2. If boundary event has `ErrorCode`, match against exception's error code. If null, catch all.
3. **Match:** Mark Call Activity as completed (with error state), create `ActivityInstanceEntry` for the `BoundaryErrorEvent`, which routes to recovery path via outgoing sequence flow
4. **No match:** Normal failure propagation

### Key Constraints

- Boundary events do NOT participate in the normal `ExecuteWorkflow()` loop
- They are stored in `WorkflowDefinition.Activities` but never added to active entries until triggered
- They complete immediately when activated (like StartEvent)

## New Interface Method

`IWorkflowExecutionContext`:

```csharp
ValueTask StartChildWorkflow(CallActivity callActivity, IActivityExecutionContext activityContext);
```

## BPMN Parsing

### CallActivity

```xml
<callActivity id="call1" calledElement="paymentProcess"
    propagateAllParentVariables="true"
    propagateAllChildVariables="false">
  <extensionElements>
    <inputMapping source="orderId" target="orderId"/>
    <outputMapping source="transactionId" target="transactionId"/>
  </extensionElements>
</callActivity>
```

- `calledElement` → `CalledProcessKey`
- `propagateAllParentVariables` → `PropagateAllParentVariables` (default `true`)
- `propagateAllChildVariables` → `PropagateAllChildVariables` (default `true`)
- `<inputMapping>` → `InputMappings`
- `<outputMapping>` → `OutputMappings`

### BoundaryErrorEvent

```xml
<boundaryEvent id="err1" attachedToRef="call1">
  <errorEventDefinition errorRef="PaymentFailed"/>
</boundaryEvent>
```

- `attachedToRef` → `AttachedToActivityId`
- `errorRef` → `ErrorCode` (nullable)

## Testing

### Integration Tests (TestCluster)

1. Basic completion: parent → call activity → child completes → parent resumes
2. Input variable mapping: only mapped variables reach child
3. Output variable mapping: only mapped variables return to parent
4. PropagateAllParentVariables=true: all parent variables reach child
5. PropagateAllChildVariables=true: all child variables return to parent
6. Both flags false + no mappings: full isolation
5. Child failure without boundary event: normal error propagation
6. Child failure with catch-all boundary event: routes to recovery
7. Child failure with specific error code boundary event: matches/doesn't match
8. Sequential chaining: activity → call activity → activity

### Unit Tests

9. `CallActivity.ExecuteAsync` calls `StartChildWorkflow()`
10. `CallActivity.GetNextActivities` returns outgoing flow target
11. `BoundaryErrorEvent.GetNextActivities` returns outgoing flow target

### BpmnConverter Tests

12. Parse `<callActivity>` with mappings
13. Parse `<boundaryEvent>` with error definition
