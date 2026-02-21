# Scoped GetVariable with Explicit variablesId

**Date:** 2026-02-21
**Status:** Approved

## Problem

`GetVariable(string name)` does a backward linear search across all `WorkflowVariablesState` entries, ignoring variable scoping. In parallel branches — where each branch has its own cloned variable scope — this returns the wrong value (whichever scope was added last to the list).

## Solution

Change the signature to `GetVariable(Guid variablesId, string variableName)` on both `IWorkflowExecutionContext` and `IBoundaryEventStateAccessor`. The implementation looks up the specific `WorkflowVariablesState` by ID instead of searching all scopes.

Expose `GetVariablesStateId()` on `IActivityExecutionContext` so domain-layer activities can obtain their scope ID and pass it explicitly to `RegisterMessageSubscription` and `RegisterBoundaryMessageSubscription`.

## Changes

### 1. Domain Interfaces

- `IWorkflowExecutionContext.GetVariable(Guid variablesId, string variableName)`
- `IWorkflowExecutionContext.RegisterMessageSubscription(Guid variablesId, string messageDefinitionId, string activityId)`
- `IWorkflowExecutionContext.RegisterBoundaryMessageSubscription(Guid variablesId, Guid hostActivityInstanceId, string boundaryActivityId, string messageDefinitionId)`
- `IActivityExecutionContext.GetVariablesStateId()` — new method

### 2. Grain Interfaces

Re-declare with `[ReadOnly]` + `new` per existing ISP pattern:
- `IWorkflowInstanceGrain` — `GetVariable`
- `IActivityInstanceGrain` — `GetVariablesStateId`

### 3. Implementation — `WorkflowInstance.GetVariable`

```csharp
public ValueTask<object?> GetVariable(Guid variablesId, string variableName)
{
    var variableState = State.GetVariableState(variablesId);
    var dict = (IDictionary<string, object?>)variableState.Variables;
    return ValueTask.FromResult(dict.TryGetValue(variableName, out var value) ? value : null);
}
```

### 4. `IBoundaryEventStateAccessor` and `IBoundaryEventHandler`

- `IBoundaryEventStateAccessor.GetVariable(Guid variablesId, string variableName)`
- `IBoundaryEventHandler.UnsubscribeBoundaryMessageSubscriptionsAsync` — add `Guid variablesId` parameter

### 5. Domain Activity Callers

- `MessageIntermediateCatchEvent` — gets `variablesId` from `activityContext.GetVariablesStateId()`, passes to `RegisterMessageSubscription`
- `BoundarableActivity` — gets `variablesId` from `activityContext.GetVariablesStateId()`, passes to `RegisterBoundaryMessageSubscription`

## Design Decisions

- **Explicit over implicit**: Callers pass `variablesId` rather than relying on hidden context (RequestContext). This makes scoping obvious and testable.
- **No backward-compatible overload**: The old unscoped method is buggy. Keeping it around invites misuse.
- **Domain-visible scope ID**: `GetVariablesStateId()` promoted to `IActivityExecutionContext` so domain activities can participate in scoped variable access without depending on grain interfaces.
