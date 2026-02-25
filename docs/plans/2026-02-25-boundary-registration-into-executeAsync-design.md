# Move Boundary Event Registration into ExecuteAsync

**Date:** 2026-02-25
**Status:** Approved

## Problem

`WorkflowInstance.ExecuteActivities` has a type check after executing each activity:

```csharp
if (currentActivity is IBoundarableActivity boundarable)
{
    await boundarable.RegisterBoundaryEventsAsync(this, activityState, scopeDefinition);
}
```

This violates the pattern established by `SubProcess`, which calls `OpenSubProcessScope` inside its own `ExecuteAsync`. Boundary registration is activity-specific behavior that belongs in the activity's execution lifecycle, not in the orchestrator.

## Design

Move `RegisterBoundaryEventsAsync` into `BoundarableActivity.ExecuteAsync` as a template-method override, then delete the `IBoundarableActivity` interface and the type check in `WorkflowInstance`.

### Call chain after refactoring

```
TaskActivity (inherits BoundarableActivity.ExecuteAsync)
  -> Activity.ExecuteAsync (Execute + PublishEvent)
  -> RegisterBoundaryEventsAsync

SubProcess.ExecuteAsync
  -> base.ExecuteAsync (BoundarableActivity)
    -> Activity.ExecuteAsync (Execute + PublishEvent)
    -> RegisterBoundaryEventsAsync
  -> OpenSubProcessScope
```

### Changes

1. **`BoundarableActivity`** — add `ExecuteAsync` override that calls `base.ExecuteAsync()` then `RegisterBoundaryEventsAsync()`. Make `RegisterBoundaryEventsAsync` private.
2. **`SubProcess`** — simplify `ExecuteAsync` to call `base.ExecuteAsync()` (replacing duplicated Execute + PublishEvent code), then `OpenSubProcessScope`.
3. **`WorkflowInstance.ExecuteActivities`** — delete the `IBoundarableActivity` type check block.
4. **Delete `IBoundarableActivity.cs`** — no longer needed.
5. **`TaskActivity`** — no changes needed (inherits BoundarableActivity override).
6. **Tests** — adjust `BoundarableActivityTests` if they call `RegisterBoundaryEventsAsync` directly (now private). Integration tests unchanged.

### Ordering change

Previously: `ExecuteAsync` then `RegisterBoundaryEventsAsync` (separate step).
Now: boundary events registered inside `ExecuteAsync`, before `SubProcess.OpenSubProcessScope`.

This is more correct — boundary events should be listening before child activities start.
