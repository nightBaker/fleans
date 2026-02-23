# State Durability Fix: Persist After Each Transition

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix critical state loss bug (C2) — persist workflow state after each transition inside the execution loop.

**Architecture:** Add `WriteStateAsync()` after `TransitionToNextActivity()` in the `ExecuteWorkflow()` loop. Add a `[LoggerMessage]` for observability. Update risk audit doc.

**Tech Stack:** Orleans `IPersistentState<T>`, `[LoggerMessage]` source generators

---

**Date:** 2026-02-23
**Status:** Approved
**Risk:** C2 (Critical) — grain state loss on silo crashes

## Problem

`WorkflowInstance.ExecuteWorkflow()` runs a `while(AnyNotExecuting)` loop that can execute multiple activities per invocation. State is persisted only once at the end of the public entry point (e.g., `StartWorkflow`, `CompleteActivity`). If the Orleans silo crashes mid-loop, all intermediate transitions are lost.

### Example

Workflow: Start → ScriptTask1 → ScriptTask2 → UserTask

1. `StartWorkflow()` calls `ExecuteWorkflow()`
2. Loop iteration 1: Start executes → transitions to ScriptTask1
3. Loop iteration 2: ScriptTask1 executes → transitions to ScriptTask2
4. Loop iteration 3: ScriptTask2 executes → transitions to UserTask (waits for external input)
5. Loop exits, `WriteStateAsync()` called — **this is the only write**

If the silo crashes at step 3, iterations 1–2 are lost. On rehydration, the workflow restarts from the initial state.

## Fix

Add `await _state.WriteStateAsync()` after `TransitionToNextActivity()` inside the `ExecuteWorkflow()` loop.

### Why persist after TransitionToNextActivity

At this point, state is consistent:
- Completed entries are marked completed (`State.CompleteEntries`)
- New activity entries are added (`State.AddEntries`)
- ActivityInstanceGrains for new entries are already initialized (SetActivity, SetVariablesId)
- Every entry in state references a fully set-up grain

### Edge cases

| Scenario | Outcome |
|----------|---------|
| Crash between WriteStateAsync and next loop iteration | Safe — rehydration picks up from persisted state, loop continues |
| Crash during TransitionToNextActivity before state modification | Safe — state unchanged, rehydration finds last persisted state. Orphaned ActivityInstanceGrains are a minor leak, not a correctness issue |
| Duplicate write at public entry point | Harmless — no state changed between loop's final write and caller's write |
| Activity-level writes inside the loop (e.g., AddConditionSequenceStates) | No conflict — transition write adds on top |

### Performance impact

- +1 DB write per loop iteration
- Only affects workflows with auto-completing activities chained together (Script → Script → Script)
- Workflows waiting on external events (most real-world cases): loop runs 1 iteration, so +1 write total
- Acceptable trade-off for correctness

## Audit Corrections

During the investigation, the following audit findings were verified:

| ID | Claim | Verified? |
|----|-------|-----------|
| C2 | State loss on silo crashes | **Confirmed** — real and severe |
| C3 | Parallel gateway join race conditions | **False alarm** — Orleans turn-based concurrency prevents this; grain is not `[Reentrant]` |
| H3 | Signal/message delivery fire-and-forget | **Confirmed** — subscriptions cleared before delivery; failures = lost signals |

---

## Implementation Plan

### Task 1: Add WriteStateAsync and log message

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs:59-79` (ExecuteWorkflow method)
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs:831` (add LoggerMessage after EventId 3005)

**Step 1: Add `WriteStateAsync()` after `TransitionToNextActivity()`**

In `ExecuteWorkflow()` (line 77), add the persist call after transition:

```csharp
private async Task ExecuteWorkflow()
{
    var definition = await GetWorkflowDefinition();
    while (await AnyNotExecuting())
    {
        foreach (var activityState in await GetNotExecutingNotCompletedActivities())
        {
            var activityId = await activityState.GetActivityId();
            var currentActivity = definition.GetActivity(activityId);
            SetActivityRequestContext(activityId, activityState);
            LogExecutingActivity(activityId, currentActivity.GetType().Name);
            await currentActivity.ExecuteAsync(this, activityState, definition);
            if (currentActivity is IBoundarableActivity boundarable)
            {
                await boundarable.RegisterBoundaryEventsAsync(this, activityState, definition);
            }
        }

        await TransitionToNextActivity();
        LogStatePersistedAfterTransition();
        await _state.WriteStateAsync();
    }
}
```

**Step 2: Add the `[LoggerMessage]` declaration**

After the `LogStateCompleteEntries` declaration (EventId 3005), add:

```csharp
[LoggerMessage(EventId = 3006, Level = LogLevel.Debug, Message = "State persisted after transition")]
private partial void LogStatePersistedAfterTransition();
```

**Step 3: Run tests to verify no regression**

Run: `dotnet test src/Fleans/`
Expected: All existing tests pass.

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "fix: persist state after each transition in execution loop (C2)"
```

### Task 2: Commit design doc and risk audit update

**Files:**
- Already modified: `docs/plans/2026-02-23-state-durability-fix.md`
- Already modified: `docs/plans/2026-02-17-architectural-risk-audit.md`

**Step 1: Commit docs**

```bash
git add docs/plans/2026-02-23-state-durability-fix.md docs/plans/2026-02-17-architectural-risk-audit.md
git commit -m "docs: add state durability design doc, update risk audit with runtime risks"
```
