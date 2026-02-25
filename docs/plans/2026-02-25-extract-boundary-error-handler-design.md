# Extract Boundary Error Handler Query to Domain

**Date:** 2026-02-25
**Status:** Approved

## Problem

`WorkflowInstance.FailActivityWithBoundaryCheck` contains a domain query (find matching `BoundaryErrorEvent` by activity ID and error code, walking up scope hierarchy) mixed with infrastructure work (fail grains, cancel scope children, invoke handler). The domain query should live on `IWorkflowDefinition`.

Additionally, two pre-existing bugs are fixed:

1. **Wrong scope definition passed in direct match case** — Line 652 passes root `definition` instead of the scope where the boundary was found. For boundary events inside SubProcesses, `GetNextActivities` would fail to find outgoing sequence flows. The bubbling case (line 677) correctly passes `scopeParentDef`.

2. **Non-deterministic match priority** — `FirstOrDefault` picks whichever `BoundaryErrorEvent` appears first. If an activity has both a specific-code boundary (e.g., "500") and a catch-all (null), the result depends on BPMN parse order. BPMN spec requires specific codes to take priority over catch-all.

## Design

Add a default interface method to `IWorkflowDefinition`:

```csharp
(BoundaryErrorEvent BoundaryEvent, IWorkflowDefinition Scope, string AttachedToActivityId)?
    FindBoundaryErrorHandler(string failedActivityId, string? errorCode)
```

Returns the matching boundary error event, the scope definition containing it, and the activity ID it's attached to. Returns null if no matching boundary found in any scope.

### Algorithm

1. Set `targetActivityId = failedActivityId`
2. Find the scope containing `targetActivityId` via `FindScopeForActivity`
3. Search that scope's activities for `BoundaryErrorEvent` where `AttachedToActivityId == targetActivityId` and error code matches
4. **Priority**: prefer specific error code match over catch-all (null ErrorCode)
5. If found, return `(boundaryEvent, scope, targetActivityId)`
6. If not found and scope is a `SubProcess`, set `targetActivityId = subProcess.ActivityId` and repeat from step 2
7. If at root scope with no match, return null

### Changes

1. **`IWorkflowDefinition`** — add default interface method `FindBoundaryErrorHandler`
2. **`WorkflowInstance.FailActivityWithBoundaryCheck`** — replace inline LINQ + scope walking (lines 643-682) with `definition.FindBoundaryErrorHandler(activityId, errorCode)`. Infrastructure loop for cancelling intermediate sub-process scopes remains but is simplified.
3. **Tests** — unit tests for the domain method covering: direct match, no match, catch-all, specific code priority over catch-all, code mismatch, bubbled match through SubProcess, activity at root scope.
