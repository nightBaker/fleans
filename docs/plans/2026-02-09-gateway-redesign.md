# Gateway Redesign

## Problem

Three issues with the current gateway implementation:

1. **ExclusiveGateway has no default flow.** If no condition evaluates to `true`, `GetNextActivities` returns an empty list and the workflow silently hangs.

2. **Gateway base class is ExclusiveGateway-biased.** `SetConditionResult` only makes sense for condition-based gateways but lives on the base. ParallelGateway doesn't use conditions at all.

3. **`SetConditionResult` doesn't trigger workflow continuation.** After all conditions are evaluated via `CompleteConditionSequence`, nobody calls `ExecuteWorkflow()`. The only reason tests pass is that they manually call `CompleteActivity` afterward — an artificial step that wouldn't happen in production where conditions are evaluated asynchronously via events.

## Design

### Type Hierarchy

```
Activity (abstract)
  └── Gateway (abstract, thin marker — no methods)
        ├── ConditionalGateway (abstract, condition evaluation + auto-completion)
        │     └── ExclusiveGateway
        └── ParallelGateway
```

- **`Gateway`** becomes an empty abstract record. The current `SetConditionResult` is removed from here.
- **`ConditionalGateway`** is new. Holds condition evaluation logic and auto-completion detection shared by condition-based gateways (Exclusive now, Inclusive in the future).
- **`ExclusiveGateway`** extends `ConditionalGateway`. Overrides `GetNextActivities` to pick the first true path or the default flow.
- **`ParallelGateway`** extends `Gateway` directly — no condition logic.

### DefaultSequenceFlow

New type extending `SequenceFlow`:

```csharp
[GenerateSerializer]
public record DefaultSequenceFlow(string SequenceFlowId, Activity Source, Activity Target)
    : SequenceFlow(SequenceFlowId, Source, Target);
```

No condition, no special properties — its type alone carries the semantic meaning. Maps to the BPMN `default` attribute on gateway elements:

```xml
<exclusiveGateway id="gw1" default="seq_fallback" />
<sequenceFlow id="seq_fallback" sourceRef="gw1" targetRef="task2" />
```

### ConditionalGateway Auto-Completion

`SetConditionResult` moves to `ConditionalGateway` and returns `bool` indicating whether the gateway has made its routing decision:

```csharp
internal async Task<bool> SetConditionResult(
    IWorkflowInstance workflowInstance,
    IActivityInstance activityInstance,
    string conditionSequenceFlowId,
    bool result)
{
    // 1. Store the result in state
    var state = await workflowInstance.GetState();
    var activityInstanceId = await activityInstance.GetActivityInstanceId();
    await state.SetConditionSequencesResult(activityInstanceId, conditionSequenceFlowId, result);

    // 2. Short-circuit: first true → done
    if (result)
        return true;

    // 3. Check if all conditions are now evaluated
    var sequences = await state.GetConditionSequenceStates();
    var mySequences = sequences[activityInstanceId];

    if (mySequences.All(s => s.IsEvaluated))
    {
        // All false — need a default flow
        var definition = await workflowInstance.GetWorkflowDefinition();
        var hasDefault = definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .Any(sf => sf.Source.ActivityId == activityInstance.GetCurrentActivity().Result.ActivityId);

        if (!hasDefault)
            throw new InvalidOperationException(
                "All conditions evaluated to false and no default flow exists");

        return true;
    }

    // 4. Still waiting
    return false;
}
```

### ConditionSequenceState.IsEvaluated

`ConditionSequenceState` gains an `IsEvaluated` property to distinguish "not yet evaluated" from "evaluated to false". Set to `true` when `SetResult` is called.

### WorkflowInstance.CompleteConditionSequence

Updated to use the boolean return value and resume the workflow:

```csharp
public async Task CompleteConditionSequence(
    string activityId, string conditionSequenceId, bool result)
{
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();
    LogConditionResult(conditionSequenceId, result);

    var activityInstance = await State.GetFirstActive(activityId)
        ?? throw new InvalidOperationException("Active activity not found");

    var gateway = await activityInstance.GetCurrentActivity() as ConditionalGateway
        ?? throw new InvalidOperationException("Activity is not a conditional gateway");

    var isDecisionMade = await gateway.SetConditionResult(
        this, activityInstance, conditionSequenceId, result);

    if (isDecisionMade)
    {
        await activityInstance.Complete();
        await ExecuteWorkflow();
    }
}
```

No variable merge — gateways are routing decisions, not data producers. Variable state passes through unchanged via `variablesId` propagation in `TransitionToNextActivity`.

### ExclusiveGateway.GetNextActivities

Updated to handle the default flow case:

- Check conditional flows for `true` results. If found, return that target (first match).
- If none found (all `false`), find the `DefaultSequenceFlow` from outgoing flows and return its target.

### BpmnConverter Changes

1. When parsing `<exclusiveGateway>`, read the `default` attribute and store: `gatewayDefaults[gatewayId] = defaultFlowId`.
2. When building sequence flows, if a flow's ID is in `gatewayDefaults` values, create a `DefaultSequenceFlow` instead of a plain `SequenceFlow`.
3. No changes to conditional flow parsing.

## Tests

### ExclusiveGateway Tests (updated)

Remove manual `CompleteActivity` calls — the gateway now auto-completes via `CompleteConditionSequence`.

New tests:
- **Short-circuit**: first condition returns `true` → workflow completes immediately without waiting for remaining conditions
- **Default flow**: all conditions return `false` → workflow takes the default flow path
- **No default flow, all false**: all conditions `false`, no default → throws `InvalidOperationException`

### ConditionSequenceState Tests

- `IsEvaluated` starts as `false`
- Becomes `true` after `SetResult(true)` or `SetResult(false)`

### ConditionalGateway.SetConditionResult Tests

- Returns `true` on first `true` condition
- Returns `false` when some conditions still pending
- Returns `true` when all `false` and default flow exists
- Throws when all `false` and no default flow

## Logging Requirements

All workflow instance state changes must be logged using `[LoggerMessage]` source generators. Every new method or significant state transition introduced by this redesign must have a corresponding log message.

### ConditionalGateway (EventId range: 8000)

- **8000** Condition result stored: `"Condition {ConditionSequenceFlowId} evaluated to {Result} for activity {ActivityId}"`
- **8001** Gateway short-circuited on first true: `"Gateway {ActivityId} short-circuited: condition {ConditionSequenceFlowId} is true"`
- **8002** All conditions false, taking default flow: `"Gateway {ActivityId} all conditions false, taking default flow"`
- **8003** All conditions false, no default flow: `"Gateway {ActivityId} all conditions false and no default flow — misconfigured workflow"` (LogLevel.Error, before throwing)

### WorkflowInstance.CompleteConditionSequence (existing EventId range: 1000)

- **1007** Gateway decision made, auto-completing: `"Gateway {ActivityId} decision made, auto-completing and resuming workflow"`

### General Rule

Every grain method that mutates state (adds/removes activities, changes condition results, completes/fails instances) must log the change. This applies to all new code in this redesign and is a standing rule for all future workflow instance changes.

## Out of Scope

- Full activity lifecycle fail tests (error codes 400/500) for gateways — gateways are routing nodes, not user-logic executors
- ParallelGateway fixes (known join logic issues tracked separately)
- InclusiveGateway implementation (future — `ConditionalGateway` base prepared for it)
