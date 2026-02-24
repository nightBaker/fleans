# Nested Scopes — Embedded SubProcess Design

_Date: 2026-02-24_

## Problem

BPMN requires support for embedded sub-processes: a group of activities contained
within a parent process, sharing the same workflow instance but executing in an
isolated variable scope. Previous attempts (PR #96) used ad-hoc scope lookups
that complicated runtime activity resolution.

## Solution: First-Class `WorkflowScopeState` Aggregate

### Domain Model

```
WorkflowScopeState
├── ScopeId             — unique identity of this scope instance
├── ParentScopeId       — parent scope (null if directly under root process)
├── VariablesId         — the child WorkflowVariablesState id bound to this scope
├── SubProcessActivityId      — the sub-process activity definition id
├── SubProcessActivityInstanceId — the grain instance id for the sub-process activity
└── ActiveChildInstanceIds  — HashSet<Guid>; all tracked active child instances
```

A scope is opened when a `SubProcess` activity executes. It is closed when
`ActiveChildInstanceIds` reaches zero (O(1) completion check — no iterative scan).

### Variable Scoping

`WorkflowVariablesState` gains a `ParentVariablesId` pointer:

- **Reads** walk up the parent chain (inheritance / visibility through parent scope).
- **Writes** always go to the local scope (shadowing — no mutation of parent state).
- Scripts (`GetVariables`) receive a fully-merged ExpandoObject that overlays parent
  scope variables with local overrides.

### Activity Entry Ownership

`ActivityInstanceEntry` gains a `ScopeId` field:

- Root-level entries have `ScopeId = null`.
- Entries spawned inside a sub-process have `ScopeId = <owning scope id>`.

This drives scope tracking: on each `TransitionToNextActivity` pass,
`UntrackChild` / `TrackChild` are called on the owning scope to maintain the
`ActiveChildInstanceIds` counter. The sub-process activity instance is completed
automatically when the counter reaches zero.

### Scope-Aware Definition Resolution

`GetDefinitionForEntry(entry, rootDefinition)` resolves the `IWorkflowDefinition`
for any entry by recursively walking the scope tree:

```
GetDefinitionForScope(scopeId, root):
  if scopeId == null → return root
  scope = State.GetScope(scopeId)
  parentDef = GetDefinitionForScope(scope.ParentScopeId, root)
  return (SubProcess) parentDef.GetActivity(scope.SubProcessActivityId)
```

All activity execution, boundary event registration, and transition logic uses
this resolver — no scattered `if (activity is SubProcess)` guards.

## `EndEvent` Scope Awareness

`IWorkflowDefinition` gains a default property `IsRootProcess = true`. `SubProcess`
overrides it to return `false`. `EndEvent.ExecuteAsync` only calls
`workflowContext.Complete()` (marks the whole workflow as done) when
`definition.IsRootProcess` is true. Inside a sub-process, the EndEvent completes
the activity instance only; scope completion is detected by the scope aggregate.

## Error Propagation

`FailActivityWithBoundaryCheck` now traverses scope boundaries:

1. Check for a `BoundaryErrorEvent` in the activity's own scope definition.
2. If not found and the activity is inside a scope, look for a `BoundaryErrorEvent`
   attached to the sub-process in the parent definition.
3. If found at sub-process level: cancel all scope children (`CancelScope`),
   then fire the boundary error path.
4. If no handler at either level: the workflow stays stuck (no silent completion).

## Scope Cancellation

`CancelScope(scopeId)` is a single domain operation that:

1. Recursively drains any nested scopes (depth-first).
2. Returns all drained child instance ids.
3. Removes the scope from `State.Scopes`.

The caller (boundary timer handler or error propagation) cancels the individual
activity grains and marks their entries as completed.

## BPMN Parsing

`BpmnConverter.ParseActivities` now uses `Elements` instead of `Descendants` so
that inner sub-process elements are not leaked into the outer scope. `<subProcess>`
elements are parsed recursively: child activities and flows are collected, and a
`SubProcess` domain object is built with its own `IWorkflowDefinition`. Boundary
events on a sub-process are correctly placed in the **parent** scope's activity list
(not inside the sub-process definition) since they are attached to the sub-process
activity, not to any inner activity.

## Invariants

1. Every active `ActivityInstanceEntry` with `ScopeId != null` has a corresponding
   entry in `State.Scopes[ScopeId].ActiveChildInstanceIds`.
2. A scope with `ActiveChildInstanceIds.Count == 0` completes its sub-process
   activity instance within the same `TransitionToNextActivity` pass.
3. Scope completion only occurs when no child has an unhandled failure.
4. Writes to variables always go to the local scope; reads resolve through the chain.
5. `CancelScope` is idempotent on already-empty scopes.
