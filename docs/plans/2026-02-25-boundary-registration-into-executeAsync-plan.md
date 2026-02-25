# Move Boundary Registration into ExecuteAsync — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Move boundary event registration from the WorkflowInstance orchestrator into `BoundarableActivity.ExecuteAsync`, delete `IBoundarableActivity`, and simplify `SubProcess.ExecuteAsync`.

**Architecture:** Template-method pattern — `BoundarableActivity` overrides `ExecuteAsync` to call `base.ExecuteAsync()` (Activity) then `RegisterBoundaryEventsAsync()`. Subclasses (`SubProcess`, `CallActivity`) chain via `base.ExecuteAsync()`.

**Tech Stack:** .NET 10, C# 14, Orleans, NSubstitute, MSTest

---

### Task 1: Add ExecuteAsync override to BoundarableActivity and make RegisterBoundaryEventsAsync private

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs`

**Step 1: Add the ExecuteAsync override**

In `BoundarableActivity.cs`, add an `ExecuteAsync` override before `RegisterBoundaryEventsAsync`:

```csharp
internal override async Task ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);
    await RegisterBoundaryEventsAsync(workflowContext, activityContext, definition);
}
```

**Step 2: Make RegisterBoundaryEventsAsync private**

Change `public async Task RegisterBoundaryEventsAsync` to `private async Task RegisterBoundaryEventsAsync`.

**Step 3: Verify it compiles**

Run: `dotnet build src/Fleans/ --verbosity quiet 2>&1 | tail -5`

Expected: Build errors in `BoundarableActivityTests.cs` (calling now-private method) and `WorkflowInstance.cs` (still referencing `IBoundarableActivity`). That's expected — we fix those next.

---

### Task 2: Delete IBoundarableActivity interface and remove type check from WorkflowInstance

**Files:**
- Delete: `src/Fleans/Fleans.Domain/IBoundarableActivity.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs` (remove interface from inheritance)
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs:74-77`

**Step 1: Remove IBoundarableActivity from BoundarableActivity's inheritance**

In `BoundarableActivity.cs`, change:
```csharp
public abstract record BoundarableActivity(string ActivityId)
    : Activity(ActivityId), IBoundarableActivity
```
to:
```csharp
public abstract record BoundarableActivity(string ActivityId)
    : Activity(ActivityId)
```

**Step 2: Delete the IBoundarableActivity.cs file**

Delete: `src/Fleans/Fleans.Domain/IBoundarableActivity.cs`

**Step 3: Remove the type check from WorkflowInstance.ExecuteWorkflow**

In `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`, delete lines 74-77:

```csharp
                if (currentActivity is IBoundarableActivity boundarable)
                {
                    await boundarable.RegisterBoundaryEventsAsync(this, activityState, scopeDefinition);
                }
```

Also remove the `using Fleans.Domain;` import if it was only needed for `IBoundarableActivity` (check if other types from that namespace are still used — they likely are, so keep it).

**Step 4: Verify it compiles**

Run: `dotnet build src/Fleans/ --verbosity quiet 2>&1 | tail -5`

Expected: Build errors only in `BoundarableActivityTests.cs` (calling now-private `RegisterBoundaryEventsAsync`).

---

### Task 3: Simplify SubProcess.ExecuteAsync to call base

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/SubProcess.cs:29-46`

**Step 1: Replace SubProcess.ExecuteAsync body with base call**

Change:
```csharp
internal override async Task ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    // Publish executed event but do NOT call Complete — sub-process waits for children.
    await activityContext.Execute();
    await activityContext.PublishEvent(new Events.WorkflowActivityExecutedEvent(
        await workflowContext.GetWorkflowInstanceId(),
        definition.WorkflowId,
        await activityContext.GetActivityInstanceId(),
        ActivityId,
        GetType().Name));

    var instanceId = await activityContext.GetActivityInstanceId();
    var variablesId = await activityContext.GetVariablesStateId();
    await workflowContext.OpenSubProcessScope(instanceId, this, variablesId);
}
```

to:

```csharp
internal override async Task ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);
    var instanceId = await activityContext.GetActivityInstanceId();
    var variablesId = await activityContext.GetVariablesStateId();
    await workflowContext.OpenSubProcessScope(instanceId, this, variablesId);
}
```

Note: `base.ExecuteAsync` now calls `Activity.ExecuteAsync` (Execute + PublishEvent) then `RegisterBoundaryEventsAsync`. The comment about "do NOT call Complete" is still true — `Activity.ExecuteAsync` doesn't call Complete.

**Step 2: Verify it compiles**

Run: `dotnet build src/Fleans/ --verbosity quiet 2>&1 | tail -5`

Expected: Build errors only in `BoundarableActivityTests.cs`.

---

### Task 4: Update BoundarableActivityTests to call ExecuteAsync instead of RegisterBoundaryEventsAsync

**Files:**
- Modify: `src/Fleans/Fleans.Domain.Tests/BoundarableActivityTests.cs`

**Step 1: Replace all RegisterBoundaryEventsAsync calls with ExecuteAsync**

In every test method, change the Act section from:
```csharp
await task.RegisterBoundaryEventsAsync(workflowContext, activityContext, definition);
```
to:
```csharp
await task.ExecuteAsync(workflowContext, activityContext, definition);
```

Same for `callActivity.RegisterBoundaryEventsAsync(...)` → `callActivity.ExecuteAsync(...)`.

This works because:
- `ExecuteAsync` is `internal` and the test project has `InternalsVisibleTo`
- The test helper already stubs `Execute()`, `PublishEvent()`, and `GetWorkflowInstanceId()` on the mocks
- `ExecuteAsync` → `Activity.ExecuteAsync` (Execute + Publish) → `RegisterBoundaryEventsAsync` — same assertions still valid

**Step 2: Build and run all tests**

Run: `dotnet build src/Fleans/ --verbosity quiet 2>&1 | tail -5`

Expected: 0 errors.

Run: `dotnet test src/Fleans/ --verbosity quiet 2>&1 | tail -15`

Expected: All 347 tests pass.

---

### Task 5: Commit

**Step 1: Stage and commit all changes**

```bash
git add \
  src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs \
  src/Fleans/Fleans.Domain/Activities/SubProcess.cs \
  src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs \
  src/Fleans/Fleans.Domain.Tests/BoundarableActivityTests.cs
git rm src/Fleans/Fleans.Domain/IBoundarableActivity.cs
git commit -m "refactor: move boundary registration into BoundarableActivity.ExecuteAsync

Eliminates IBoundarableActivity interface and type check in WorkflowInstance.
Boundary registration now happens inside the activity's ExecuteAsync lifecycle,
matching the SubProcess.OpenSubProcessScope pattern."
```
