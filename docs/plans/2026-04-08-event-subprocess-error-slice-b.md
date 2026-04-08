# Event Sub-Process Slice #B — Error Event Sub-Process (Interrupting) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Runtime execution of **interrupting, error-triggered** Event Sub-Processes — when an activity inside a scope fails with an error code, a matching `EventSubProcess` containing an `ErrorStartEvent` in that scope (or an enclosing scope) is activated: siblings are cancelled, the handler runs in a child variable scope, and the parent scope winds down normally.

**Architecture:** Reactive discovery in the error-bubbling path (no upfront registration — same pattern as `BoundaryErrorEvent`). The `EventSubProcess` becomes a new scope-container entry (`ActivitySpawned`) owned by the enclosing scope, with a fresh child variable scope. Its `ErrorStartEvent` is spawned inside and flows through the existing transition machinery. Completion rides on extending `CompleteFinishedSubProcessScopes()` to recognize `EventSubProcess`. Priority rule: a `BoundaryErrorEvent` on the failing activity wins over an `EventSubProcess` in the same scope.

**Tech Stack:** .NET 10, C# 14, Orleans 9.2.1, MSTest, Orleans.TestingHost. Feature branch `feature/event-subprocess-error` → PR to `main`, closes #264.

**Parent issue / design:** #227 (design v2), #264 (slice scope). Slice #A (#249) already landed `EventSubProcess` + `ErrorStartEvent` domain types and parsing.

---

## File Structure

**Create:**
- `src/Fleans/Fleans.Domain.Tests/WorkflowDefinitionErrorEventSubProcessTests.cs` — unit tests for the new `FindErrorEventSubProcessHandler` method on `IWorkflowDefinition`.
- `src/Fleans/Fleans.Domain.Tests/WorkflowExecutionErrorEventSubProcessTests.cs` — aggregate-level tests for `FailActivity` triggering an error event sub-process (cancellation, spawn, completion, nested scopes, precedence vs `BoundaryErrorEvent`, long-running sibling).
- `tests/manual/19-event-subprocess-error/error-event-subprocess.bpmn` — manual test fixture.
- `tests/manual/19-event-subprocess-error/test-plan.md` — manual test plan.

**Modify:**
- `src/Fleans/Fleans.Domain/Definitions/Workflow.cs` — add `FindErrorEventSubProcessHandler(string failedActivityId, string? errorCode)` to `IWorkflowDefinition` (bubbles up through `SubProcess` and `EventSubProcess` scopes; specific-code match preferred over catch-all).
- `src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`:
  - `FailActivity` (≈line 862): after `FindBoundaryErrorHandler` returns null, call `FindErrorEventSubProcessHandler`; if matched, run the new error-event-subprocess activation flow.
  - `CompleteFinishedSubProcessScopes` (≈line 586–672): recognize `EventSubProcess` as a scope-host type (same branch as `SubProcess`), so the handler-scope auto-completes when its children are terminal. Child variable scope merge MUST NOT write back into the parent scope (semantic: event sub-process sees a snapshot, no merge).
- `src/Fleans/Fleans.Domain/Definitions/Workflow.cs` — extend `FindScopeForActivity` to also descend into `EventSubProcess.Activities` (needed so activities declared inside an event sub-process resolve to their correct scope once we start spawning them).

---

## Key Semantics (lock these in before coding)

1. **Precedence:** When an activity fails, search in this order and take the first match:
   1. `BoundaryErrorEvent` attached to the failing activity or any enclosing `SubProcess` host (existing `FindBoundaryErrorHandler` — unchanged).
   2. `EventSubProcess` containing an `ErrorStartEvent` in the failing activity's scope, bubbling up through parent `SubProcess` scopes.
   - Specific `ErrorCode` beats catch-all (null `ErrorCode`) at each scope level. Inner scope beats outer scope.
2. **EventSubProcess scope entry:** On match, emit `ActivitySpawned` for the `EventSubProcess` itself with:
   - `ActivityInstanceId` = new GUID → becomes the scope container id.
   - `ActivityType = "EventSubProcess"`.
   - `VariablesId` = parent scope's `VariablesId` (the scope that owns the EventSubProcess definition — resolved from the entry whose `ActivityInstanceId` equals the cancellation target, or from the root `VariableStates` when the EventSubProcess lives at root).
   - `ScopeId` = the `ScopeId` of the siblings being cancelled (i.e. the enclosing scope container id — `null` at root).
3. **Child variable scope:** Emit `ChildVariableScopeCreated(newScopeId, parentVariablesId)`. Spawn `ErrorStartEvent` with `VariablesId = newScopeId`, `ScopeId = <EventSubProcess instance id>`. **Do not merge this scope back on completion** (see modifications to `CompleteFinishedSubProcessScopes`).
4. **Cancellation target:** "Siblings to cancel" = all active entries with `ScopeId` equal to the EventSubProcess's enclosing scope container id. This may include an intermediate `SubProcess` host that transitively contains the failing activity; `CancelScopeChildren` already recurses through nested scopes, so calling it on the enclosing scope container id is sufficient.
5. **ActivityFailed + cancellation ordering:** Emit `ActivityFailed` for the failing activity first (it is already terminal), THEN cancel remaining active entries in the enclosing scope (the failing entry is already terminal and will be skipped by `CancelScopeChildren`'s "active" filter). The EventSubProcess spawn comes last. This matches how `FailActivity` handles the boundary error case today.
6. **Peer deregistration:** Slice B ships only error-triggered event sub-processes (reactive; no subscriptions). There are no peer timer/message/signal listener subscriptions to clean up yet. Add an explicit TODO comment at the peer-deregistration site pointing at slices #C–#E.
7. **Cancelled siblings + scope completion:** `AllChildrenOfScopeFinished` already treats `Cancelled` entries as terminal — no change needed. After the handler completes, `CompleteFinishedSubProcessScopes` marks the EventSubProcess entry `Completed`, which in turn lets the enclosing scope (root workflow or a `SubProcess`) finish via the existing recursive sweep.
8. **Child workflow / user task siblings:** Cancellation recursion already emits `BuildUserTaskCleanupEffects` per entry. Child workflow cancellation is out of scope for this slice (existing behaviour is whatever `CancelScopeChildren` does today — do not add new cleanup here).
9. **Error not found → existing behaviour:** If neither a boundary handler nor an error event sub-process matches, `FailActivity` continues into the existing MI-host-fail / notify-parent path unchanged.

---

## Task 1: `FindErrorEventSubProcessHandler` on `IWorkflowDefinition` (definition-level search, pure)

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Definitions/Workflow.cs` (add a default interface method alongside `FindBoundaryErrorHandler`, ≈line 159; also extend `FindScopeForActivity` at lines 87–110).
- Test: `src/Fleans/Fleans.Domain.Tests/WorkflowDefinitionErrorEventSubProcessTests.cs` (new file).

- [ ] **Step 1: Write failing tests for `FindErrorEventSubProcessHandler`**

Create `src/Fleans/Fleans.Domain.Tests/WorkflowDefinitionErrorEventSubProcessTests.cs`:

```csharp
using System.Linq;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowDefinitionErrorEventSubProcessTests
{
    private static EventSubProcess BuildErrorEventSubProcess(
        string id, string? errorCode, string handlerId = "handler1")
    {
        var errStart = new ErrorStartEvent("errStart_" + id, errorCode);
        var handler = new ScriptTask(handlerId, "return 1;");
        var end = new EndEvent("errEnd_" + id);
        return new EventSubProcess(id)
        {
            Activities = [errStart, handler, end],
            SequenceFlows =
            [
                new SequenceFlow("esf1_" + id, errStart, handler),
                new SequenceFlow("esf2_" + id, handler, end),
            ],
            IsInterrupting = true,
        };
    }

    [TestMethod]
    public void ReturnsNull_WhenNoErrorEventSubProcessPresent()
    {
        var task = new ScriptTask("task1", "return 1;");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), task, new EndEvent("e")],
            SequenceFlows = [],
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "500");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void MatchesInSameScope_ByErrorCode()
    {
        var task = new ScriptTask("task1", "return 1;");
        var evtSub = BuildErrorEventSubProcess("evtSub1", "500");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), task, new EndEvent("e"), evtSub],
            SequenceFlows = [],
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreEqual("evtSub1", result.Value.EventSubProcess.ActivityId);
        Assert.AreSame(definition, result.Value.EnclosingScope);
    }

    [TestMethod]
    public void CatchAll_MatchesAnyErrorCode()
    {
        var task = new ScriptTask("task1", "return 1;");
        var evtSub = BuildErrorEventSubProcess("evtSub1", null);
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), task, new EndEvent("e"), evtSub],
            SequenceFlows = [],
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "999");

        Assert.IsNotNull(result);
        Assert.AreEqual("evtSub1", result.Value.EventSubProcess.ActivityId);
    }

    [TestMethod]
    public void SpecificCodeBeatsCatchAllInSameScope()
    {
        var task = new ScriptTask("task1", "return 1;");
        var catchAll = BuildErrorEventSubProcess("evtSubCatchAll", null, "handlerAll");
        var specific = BuildErrorEventSubProcess("evtSub500", "500", "handler500");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), task, new EndEvent("e"), catchAll, specific],
            SequenceFlows = [],
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreEqual("evtSub500", result.Value.EventSubProcess.ActivityId);
    }

    [TestMethod]
    public void BubblesUpThroughSubProcess()
    {
        var innerTask = new ScriptTask("task1", "return 1;");
        var innerStart = new StartEvent("innerStart");
        var innerEnd = new EndEvent("innerEnd");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("isf1", innerStart, innerTask),
                new SequenceFlow("isf2", innerTask, innerEnd),
            ],
        };
        var evtSub = BuildErrorEventSubProcess("evtSubOuter", "500");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), subProcess, new EndEvent("e"), evtSub],
            SequenceFlows = [],
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreEqual("evtSubOuter", result.Value.EventSubProcess.ActivityId);
        Assert.AreSame(definition, result.Value.EnclosingScope);
    }

    [TestMethod]
    public void InnerScopeBeatsOuterScope()
    {
        var innerTask = new ScriptTask("task1", "return 1;");
        var innerEvtSub = BuildErrorEventSubProcess("evtSubInner", "500", "innerHandler");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [new StartEvent("innerStart"), innerTask, new EndEvent("innerEnd"), innerEvtSub],
            SequenceFlows = [],
        };
        var outerEvtSub = BuildErrorEventSubProcess("evtSubOuter", "500", "outerHandler");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf",
            Activities = [new StartEvent("s"), subProcess, new EndEvent("e"), outerEvtSub],
            SequenceFlows = [],
        };

        var result = definition.FindErrorEventSubProcessHandler("task1", "500");

        Assert.IsNotNull(result);
        Assert.AreEqual("evtSubInner", result.Value.EventSubProcess.ActivityId);
        Assert.AreSame(subProcess, result.Value.EnclosingScope);
    }
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run from `src/Fleans/`:
```
dotnet test Fleans.Domain.Tests --filter FullyQualifiedName~WorkflowDefinitionErrorEventSubProcessTests
```
Expected: build error — `FindErrorEventSubProcessHandler` does not exist on `IWorkflowDefinition`.

- [ ] **Step 3: Extend `FindScopeForActivity` to descend into `EventSubProcess`**

In `src/Fleans/Fleans.Domain/Definitions/Workflow.cs`, update the `FindScopeForActivity` method (currently lines 87–110). After the `SubProcess` loop and before the `MultiInstanceActivity` loop, add:

```csharp
            foreach (var evtSub in Activities.OfType<EventSubProcess>())
            {
                var result = ((IWorkflowDefinition)evtSub).FindScopeForActivity(activityId);
                if (result is not null)
                    return result;
            }
```

- [ ] **Step 4: Add `FindErrorEventSubProcessHandler` default method**

In the same file, add directly after `FindBoundaryErrorHandler` (after the closing brace at ≈line 159):

```csharp
        /// <summary>
        /// Finds the matching EventSubProcess (containing an ErrorStartEvent) for a
        /// failed activity, searching the activity's scope and walking up through parent
        /// SubProcess scopes. Specific error code matches take priority over catch-all
        /// (null ErrorCode). Inner scopes take priority over outer scopes.
        /// </summary>
        (EventSubProcess EventSubProcess, IWorkflowDefinition EnclosingScope)?
            FindErrorEventSubProcessHandler(string failedActivityId, string? errorCode)
        {
            var targetActivityId = failedActivityId;

            while (true)
            {
                var scope = FindScopeForActivity(targetActivityId);
                if (scope is null) return null;

                var candidates = scope.Activities
                    .OfType<EventSubProcess>()
                    .Where(esp => esp.Activities.OfType<ErrorStartEvent>().Any(ese =>
                        ese.ErrorCode == null || ese.ErrorCode == errorCode))
                    .ToList();

                // Prefer specific error code match over catch-all
                var specific = candidates.FirstOrDefault(esp =>
                    esp.Activities.OfType<ErrorStartEvent>()
                        .Any(ese => ese.ErrorCode == errorCode));
                var catchAll = candidates.FirstOrDefault(esp =>
                    esp.Activities.OfType<ErrorStartEvent>()
                        .Any(ese => ese.ErrorCode == null));

                var match = specific ?? catchAll;
                if (match is not null)
                    return (match, scope);

                if (scope is SubProcess subProcess)
                    targetActivityId = subProcess.ActivityId;
                else if (scope is EventSubProcess outerEvtSub)
                    targetActivityId = outerEvtSub.ActivityId;
                else
                    return null;
            }
        }
```

- [ ] **Step 5: Run tests — expect green**

Run from `src/Fleans/`:
```
dotnet test Fleans.Domain.Tests --filter FullyQualifiedName~WorkflowDefinitionErrorEventSubProcessTests
```
Expected: all 6 tests PASS.

- [ ] **Step 6: Commit**

```
git add src/Fleans/Fleans.Domain/Definitions/Workflow.cs src/Fleans/Fleans.Domain.Tests/WorkflowDefinitionErrorEventSubProcessTests.cs
git commit -m "feat(domain): add FindErrorEventSubProcessHandler definition lookup"
```

---

## Task 2: Extend `CompleteFinishedSubProcessScopes` to recognize `EventSubProcess`

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs` lines 605–668.
- Test: covered by Task 3 end-to-end tests.

- [ ] **Step 1: Update scope recognition + completion logic**

In `CompleteFinishedSubProcessScopes()` at line 610, replace:

```csharp
                var isSubProcess = activity is SubProcess;
```

with:

```csharp
                var isSubProcess = activity is SubProcess;
                var isEventSubProcess = activity is EventSubProcess;
```

Replace line 614:

```csharp
                if (!isSubProcess && !isMultiInstanceHost) continue;
```

with:

```csharp
                if (!isSubProcess && !isMultiInstanceHost && !isEventSubProcess) continue;
```

Then, in the SubProcess completion branch (lines 634–667), split the variable-merge behaviour so that `EventSubProcess` does NOT merge its child variable scope into the parent. Replace the block from line 634 to line 667 with:

```csharp
                // SubProcess / EventSubProcess: all scope children must be terminal
                // (Completed or Cancelled). AllChildrenOfScopeFinished semantics are
                // already baked into IsCompleted for cancelled entries.
                if (!scopeEntries.All(e => e.IsCompleted)) continue;

                // If any scope child has an unhandled error, do NOT auto-complete
                if (scopeEntries.Any(e => e.ErrorCode is not null)) continue;

                if (isSubProcess)
                {
                    // SubProcess: merge child scope variables into parent before completing.
                    var childScopes = _state.VariableStates
                        .Where(vs => vs.ParentVariablesId == entry.VariablesId)
                        .ToList();
                    if (childScopes.Count > 1)
                        throw new InvalidOperationException(
                            $"Expected at most one child scope for subprocess host {entry.ActivityId}, found {childScopes.Count}");
                    var childScope = childScopes.FirstOrDefault();
                    if (childScope is not null)
                    {
                        Emit(new VariablesMerged(entry.VariablesId, childScope.Variables));
                        allOrphanedScopeIds.Add(childScope.Id);
                    }
                }
                else
                {
                    // EventSubProcess: discard the child variable scope (no merge back).
                    // Event sub-processes see a snapshot of parent variables; their local
                    // mutations stay isolated to the handler scope.
                    var childScopes = _state.VariableStates
                        .Where(vs => vs.ParentVariablesId == entry.VariablesId)
                        .ToList();
                    foreach (var childScope in childScopes)
                        allOrphanedScopeIds.Add(childScope.Id);
                }

                // Complete the scope host (variables arg empty — merge already done for SubProcess).
                Emit(new ActivityCompleted(
                    entry.ActivityInstanceId, entry.VariablesId, new ExpandoObject()));

                var effects = BuildBoundaryUnsubscribeEffects(entry.ActivityId, entry);
                allEffects.AddRange(effects);
                allCompletedHostIds.Add(entry.ActivityInstanceId);
                anyCompleted = true;
```

Note: `BuildBoundaryUnsubscribeEffects` on an `EventSubProcess` entry is harmless (no boundaries attached) but kept for uniformity.

- [ ] **Step 2: Build**

Run from `src/Fleans/`:
```
dotnet build Fleans.Domain
```
Expected: success, no warnings introduced.

- [ ] **Step 3: Commit**

```
git add src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs
git commit -m "feat(domain): recognize EventSubProcess in CompleteFinishedSubProcessScopes"
```

---

## Task 3: `FailActivity` triggers interrupting error event sub-process (aggregate tests first)

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs` `FailActivity` at ≈line 862.
- Test: `src/Fleans/Fleans.Domain.Tests/WorkflowExecutionErrorEventSubProcessTests.cs` (new file).

- [ ] **Step 1: Write the first failing test — error caught at root scope**

Create `src/Fleans/Fleans.Domain.Tests/WorkflowExecutionErrorEventSubProcessTests.cs`:

```csharp
using System;
using System.Dynamic;
using System.Linq;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionErrorEventSubProcessTests : WorkflowExecutionTestBase
{
    private static EventSubProcess BuildErrorEventSubProcess(
        string id, string? errorCode, out ErrorStartEvent errStart, out ScriptTask handler)
    {
        errStart = new ErrorStartEvent($"{id}_errStart", errorCode);
        handler = new ScriptTask($"{id}_handler", "return 1;");
        var end = new EndEvent($"{id}_end");
        return new EventSubProcess(id)
        {
            Activities = [errStart, handler, end],
            SequenceFlows =
            [
                new SequenceFlow($"{id}_sf1", errStart, handler),
                new SequenceFlow($"{id}_sf2", handler, end),
            ],
            IsInterrupting = true,
        };
    }

    [TestMethod]
    public void FailActivity_ErrorEventSubProcessAtRoot_SpawnsHandlerAndCancelsSiblings()
    {
        var task = new ScriptTask("task1", "return 1;");
        var evtSub = BuildErrorEventSubProcess("evtSub1", "500", out var errStart, out var handler);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, evtSub],
            [
                new("seq1", start, task),
                new("seq2", task, end),
            ]);

        AdvanceToExecuting(execution, state, "start1", "task1");
        execution.ClearUncommittedEvents();

        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");
        var effects = execution.FailActivity(
            "task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        var events = execution.GetUncommittedEvents();

        // ActivityFailed for the task
        var failed = events.OfType<ActivityFailed>().Single();
        Assert.AreEqual(taskEntry.ActivityInstanceId, failed.ActivityInstanceId);

        // ActivitySpawned entries: EventSubProcess host + ErrorStartEvent
        var spawns = events.OfType<ActivitySpawned>().ToList();
        var espSpawn = spawns.Single(s => s.ActivityId == "evtSub1");
        Assert.AreEqual("EventSubProcess", espSpawn.ActivityType);
        Assert.AreEqual(taskEntry.ScopeId, espSpawn.ScopeId); // root-level: null or root container

        var startSpawn = spawns.Single(s => s.ActivityId == "evtSub1_errStart");
        Assert.AreEqual(espSpawn.ActivityInstanceId, startSpawn.ScopeId);
        Assert.AreNotEqual(taskEntry.VariablesId, startSpawn.VariablesId,
            "ErrorStartEvent should spawn in a fresh child variable scope");

        // A child variable scope was created under the parent variables
        var childScope = events.OfType<ChildVariableScopeCreated>().Single();
        Assert.AreEqual(taskEntry.VariablesId, childScope.ParentVariablesId);
        Assert.AreEqual(startSpawn.VariablesId, childScope.NewScopeId);
    }

    [TestMethod]
    public void FailActivity_BoundaryErrorEventBeatsErrorEventSubProcess()
    {
        var task = new ScriptTask("task1", "return 1;");
        var boundaryError = new BoundaryErrorEvent("boundary-error1", "task1", "500");
        var errorHandler = new ScriptTask("boundaryHandler1", "return 'handled';");
        var evtSub = BuildErrorEventSubProcess("evtSub1", "500", out _, out _);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, boundaryError, errorHandler, evtSub],
            [
                new("seq1", start, task),
                new("seq2", task, end),
                new("seq3", boundaryError, errorHandler),
            ]);

        AdvanceToExecuting(execution, state, "start1", "task1");
        execution.ClearUncommittedEvents();

        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");
        execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        var spawns = execution.GetUncommittedEvents().OfType<ActivitySpawned>().ToList();
        Assert.IsTrue(spawns.Any(s => s.ActivityId == "boundary-error1"),
            "Boundary error handler must take precedence");
        Assert.IsFalse(spawns.Any(s => s.ActivityId == "evtSub1"),
            "EventSubProcess must NOT fire when a boundary error handler matches");
    }

    [TestMethod]
    public void FailActivity_ErrorEventSubProcessInOuterScope_CancelsInnerSubProcessEntry()
    {
        var innerTask = new ScriptTask("task1", "return 1;");
        var innerStart = new StartEvent("innerStart");
        var innerEnd = new EndEvent("innerEnd");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("isf1", innerStart, innerTask),
                new SequenceFlow("isf2", innerTask, innerEnd),
            ],
        };
        var evtSub = BuildErrorEventSubProcess("evtSubOuter", "500", out _, out _);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, subProcess, end, evtSub],
            [
                new("seq1", start, subProcess),
                new("seq2", subProcess, end),
            ]);

        AdvanceThroughSubProcessToInnerTask(execution, state, "start1", "sub1", "task1");
        execution.ClearUncommittedEvents();

        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");
        var subEntry = state.Entries.First(e => e.ActivityId == "sub1");

        execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        var events = execution.GetUncommittedEvents();
        // The failing task + the enclosing SubProcess entry are both terminal
        var cancelled = events.OfType<ActivityCancelled>().ToList();
        Assert.IsTrue(cancelled.Any(c => c.ActivityInstanceId == subEntry.ActivityInstanceId),
            "Enclosing SubProcess entry must be cancelled");

        // EventSubProcess spawned at root
        var spawns = events.OfType<ActivitySpawned>().ToList();
        var espSpawn = spawns.Single(s => s.ActivityId == "evtSubOuter");
        Assert.AreEqual(subEntry.ScopeId, espSpawn.ScopeId);
    }
}
```

**Test helpers** (`AdvanceToExecuting`, `AdvanceThroughSubProcessToInnerTask`) and the `WorkflowExecutionTestBase` base class: **check before writing new helpers** whether `WorkflowExecutionBoundaryTests.cs` or `WorkflowExecutionActivityLifecycleTests.cs` already expose equivalents. If yes, reuse them (may require making them `protected` on the shared base class). If not, add small private helpers inside the new test file that follow the existing pattern from `FailActivity_WithBoundaryErrorHandler_ShouldSpawnBoundaryErrorEvent` (line 322 of `WorkflowExecutionActivityLifecycleTests.cs`) — spawn start, mark executing, mark completed, resolve transitions.

- [ ] **Step 2: Run tests — expect compile/failure**

```
dotnet test Fleans.Domain.Tests --filter FullyQualifiedName~WorkflowExecutionErrorEventSubProcessTests
```
Expected: the new tests fail (either build or runtime — `FailActivity` does not yet discover `EventSubProcess`).

- [ ] **Step 3: Wire `FailActivity` to find and activate error event sub-processes**

In `src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`, after the `FindBoundaryErrorHandler` block inside `FailActivity` (between lines 931 and 932 — the `else` that currently handles "No boundary handler found"), insert a new branch. Restructure the method as follows:

Keep the boundary-handler branch at lines 896–931 exactly as-is. Change line 932 from `else` to a block that first tries the error event sub-process, then falls through:

```csharp
        if (boundaryHandler is not null)
        {
            // ... existing boundary-error-handler branch unchanged ...
        }
        else if (TryActivateErrorEventSubProcess(entry, errorCode, effects))
        {
            // Error event sub-process took over — nothing else to do here.
        }
        else
        {
            // No handler found — existing MI-fail / notify-parent path unchanged
            // ... existing code from line 934 onward ...
        }

        return effects.AsReadOnly();
```

Add the new helper at the bottom of the file (before `// --- Activity Lifecycle Helpers ---`):

```csharp
    /// <summary>
    /// If an EventSubProcess with a matching ErrorStartEvent exists in an enclosing
    /// scope, spawns it (interrupting) and cancels siblings. Returns true if activated.
    /// Peer timer/message/signal listener deregistration is a no-op in slice #B
    /// (no registration infrastructure yet — slices #C–#E add it).
    /// </summary>
    private bool TryActivateErrorEventSubProcess(
        ActivityInstanceEntry failedEntry, int errorCode, List<IInfrastructureEffect> effects)
    {
        var match = _definition.FindErrorEventSubProcessHandler(
            failedEntry.ActivityId, errorCode.ToString());
        if (match is null) return false;

        var (eventSubProcess, enclosingScope) = match.Value;

        // Resolve the enclosing scope's container entry id. This is the ScopeId that
        // all entries in the enclosing scope share. We derive it from the failing
        // activity's entry chain: walk up ScopeId links until we find the entry whose
        // definition is `enclosingScope` (or null at root).
        var enclosingScopeContainerId = FindEnclosingScopeContainerId(failedEntry, enclosingScope);
        var enclosingScopeVariablesId = ResolveEnclosingScopeVariablesId(
            enclosingScopeContainerId, failedEntry);

        // 1. Cancel all active siblings in the enclosing scope (the failing entry
        //    is already terminal via ActivityFailed, so it is skipped).
        //    CancelScopeChildren recurses into nested subprocess entries.
        if (enclosingScopeContainerId.HasValue)
        {
            effects.AddRange(CancelScopeChildren(enclosingScopeContainerId.Value));
        }
        else
        {
            // Root scope — cancel all active entries whose ScopeId is null
            foreach (var sibling in _state.GetActiveActivities()
                .Where(e => e.ScopeId is null && e.ActivityInstanceId != failedEntry.ActivityInstanceId)
                .ToList())
            {
                if (_state.HasActiveChildrenInScope(sibling.ActivityInstanceId))
                    effects.AddRange(CancelScopeChildren(sibling.ActivityInstanceId));

                Emit(new ActivityCancelled(
                    sibling.ActivityInstanceId,
                    $"Scope cancelled by error event sub-process '{eventSubProcess.ActivityId}'"));
                effects.AddRange(BuildUserTaskCleanupEffects(sibling.ActivityInstanceId));
            }
        }

        // 2. Spawn the EventSubProcess host as a new scope container.
        var espInstanceId = Guid.NewGuid();
        Emit(new ActivitySpawned(
            ActivityInstanceId: espInstanceId,
            ActivityId: eventSubProcess.ActivityId,
            ActivityType: nameof(EventSubProcess),
            VariablesId: enclosingScopeVariablesId,
            ScopeId: enclosingScopeContainerId,
            MultiInstanceIndex: null,
            TokenId: null));

        // 3. Create a fresh child variable scope for the handler.
        var handlerScopeId = Guid.NewGuid();
        Emit(new ChildVariableScopeCreated(handlerScopeId, enclosingScopeVariablesId));

        // 4. Spawn the ErrorStartEvent inside the EventSubProcess scope.
        var errorStart = eventSubProcess.Activities.OfType<ErrorStartEvent>().First();
        Emit(new ActivitySpawned(
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: errorStart.ActivityId,
            ActivityType: nameof(ErrorStartEvent),
            VariablesId: handlerScopeId,
            ScopeId: espInstanceId,
            MultiInstanceIndex: null,
            TokenId: null));

        // TODO(slices #C–#E): when peer event-sub listener registrations are introduced,
        // emit unsubscribe effects for peer listeners on the enclosing scope here.

        return true;
    }

    /// <summary>
    /// Walks up from the failing entry's ScopeId chain until it finds the entry whose
    /// activity definition is `enclosingScope`, or returns null if the enclosing scope
    /// is the root workflow definition.
    /// </summary>
    private Guid? FindEnclosingScopeContainerId(
        ActivityInstanceEntry failedEntry, IWorkflowDefinition enclosingScope)
    {
        if (enclosingScope is WorkflowDefinition) return null;

        // enclosingScope is a SubProcess (or EventSubProcess) — find the entry whose
        // ActivityId matches the enclosing scope's identifier.
        var targetScopeActivityId = enclosingScope switch
        {
            SubProcess sp => sp.ActivityId,
            EventSubProcess esp => esp.ActivityId,
            _ => throw new InvalidOperationException(
                $"Unexpected enclosing scope type {enclosingScope.GetType().Name}"),
        };

        var current = failedEntry;
        while (current.ScopeId.HasValue)
        {
            var parent = _state.GetEntry(current.ScopeId.Value);
            if (parent.ActivityId == targetScopeActivityId)
                return parent.ActivityInstanceId;
            current = parent;
        }
        throw new InvalidOperationException(
            $"Could not locate enclosing scope entry for '{targetScopeActivityId}' from failing entry '{failedEntry.ActivityId}'");
    }

    /// <summary>
    /// Resolves the VariablesId that the EventSubProcess host should be attached to
    /// (i.e. the variables of its enclosing scope).
    /// </summary>
    private Guid ResolveEnclosingScopeVariablesId(
        Guid? enclosingScopeContainerId, ActivityInstanceEntry failedEntry)
    {
        if (enclosingScopeContainerId.HasValue)
        {
            var containerEntry = _state.GetEntry(enclosingScopeContainerId.Value);
            return containerEntry.VariablesId;
        }
        // Root scope — use the root variables id
        return _state.GetRootVariablesId();
    }
```

**Check before writing**: `_state.GetEntry(Guid)` and `_state.GetRootVariablesId()` — confirm they exist on `WorkflowExecutionState`. If `GetEntry` is absent, fall back to `_state.FindEntry` + null-check or iterate `_state.Entries`. If `GetRootVariablesId` is absent, use `_state.VariableStates.First(v => v.ParentVariablesId is null).Id`.

- [ ] **Step 4: Run the Task 3 tests — expect green**

```
dotnet test Fleans.Domain.Tests --filter FullyQualifiedName~WorkflowExecutionErrorEventSubProcessTests
```
Expected: 3 tests PASS.

- [ ] **Step 5: Add completion + precedence + long-running sibling tests**

Append to `WorkflowExecutionErrorEventSubProcessTests.cs`:

```csharp
    [TestMethod]
    public void FailActivity_ErrorEventSubProcess_HandlerCompletes_ParentWindsDown()
    {
        var task = new ScriptTask("task1", "return 1;");
        var evtSub = BuildErrorEventSubProcess("evtSub1", "500", out var errStart, out var handler);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, task, end, evtSub],
            [
                new("seq1", start, task),
                new("seq2", task, end),
            ]);

        AdvanceToExecuting(execution, state, "start1", "task1");
        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");

        // Fail the task -> triggers error event sub-process
        execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        // Drive the handler to completion: errStart -> handler -> errEnd
        AdvanceSpawnedChainToEnd(execution, state, "evtSub1_errStart", handler.ActivityId, "evtSub1_end");

        // Scope sweep should now complete the EventSubProcess entry and the workflow.
        execution.CompleteFinishedSubProcessScopes();

        var events = execution.GetUncommittedEvents();
        Assert.IsTrue(events.OfType<ActivityCompleted>().Any(c =>
            state.Entries.Any(en => en.ActivityInstanceId == c.ActivityInstanceId
                                    && en.ActivityId == "evtSub1")),
            "EventSubProcess host must be marked completed");
        Assert.IsTrue(events.OfType<WorkflowCompleted>().Any(),
            "Workflow must complete after handler finishes");
    }

    [TestMethod]
    public void FailActivity_ErrorEventSubProcess_LongRunningSiblingUserTask_IsCancelled()
    {
        var task = new ScriptTask("task1", "throw new Exception(\"boom\");");
        var userTask = new TaskActivity("userTask1");
        var parallelGw = new ParallelGateway("split");
        var joinGw = new ParallelGateway("join");
        var evtSub = BuildErrorEventSubProcess("evtSub1", "500", out _, out _);
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var (execution, state, _) = CreateStartedExecution(
            [start, parallelGw, task, userTask, joinGw, end, evtSub],
            [
                new("seq1", start, parallelGw),
                new("seq2", parallelGw, task),
                new("seq3", parallelGw, userTask),
                new("seq4", task, joinGw),
                new("seq5", userTask, joinGw),
                new("seq6", joinGw, end),
            ]);

        AdvanceToParallelBranchesExecuting(execution, state, "start1", "split", "task1", "userTask1");
        var taskEntry = state.GetActiveActivities().First(e => e.ActivityId == "task1");
        var userTaskEntry = state.GetActiveActivities().First(e => e.ActivityId == "userTask1");
        execution.ClearUncommittedEvents();

        execution.FailActivity("task1", taskEntry.ActivityInstanceId, new Exception("boom"));

        var cancelled = execution.GetUncommittedEvents().OfType<ActivityCancelled>().ToList();
        Assert.IsTrue(cancelled.Any(c => c.ActivityInstanceId == userTaskEntry.ActivityInstanceId),
            "Long-running sibling user task must be cancelled by error event sub-process");
    }
```

Helper `AdvanceSpawnedChainToEnd` / `AdvanceToParallelBranchesExecuting` / `AdvanceThroughSubProcessToInnerTask`: add them as private static helpers at the bottom of the test file if not already present on the shared test base. Each helper mirrors the explicit `MarkExecuting` → `MarkCompleted` → `ResolveTransitions` pattern from `FailActivity_WithBoundaryErrorHandler_ShouldSpawnBoundaryErrorEvent`.

- [ ] **Step 6: Run all new tests — expect green**

```
dotnet test Fleans.Domain.Tests --filter FullyQualifiedName~WorkflowExecutionErrorEventSubProcessTests
```
Expected: 5 tests PASS.

- [ ] **Step 7: Run the full domain test suite**

```
dotnet test Fleans.Domain.Tests
```
Expected: all tests PASS (no regression in existing `FailActivity_*`, `BoundaryErrorEvent`, or `CompleteFinishedSubProcessScopes` tests).

- [ ] **Step 8: Commit**

```
git add src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs src/Fleans/Fleans.Domain.Tests/WorkflowExecutionErrorEventSubProcessTests.cs
git commit -m "feat(domain): activate interrupting error event sub-process on FailActivity"
```

---

## Task 4: End-to-end integration test (Orleans TestCluster)

**Files:**
- Test: add a test in `src/Fleans/Fleans.Application.Tests/` (same project / test base as existing `ScriptTaskTests.cs`, `TaskActivityTests.cs`).

- [ ] **Step 1: Locate the closest existing integration test**

Read `src/Fleans/Fleans.Application.Tests/ScriptTaskTests.cs` for the TestCluster + `workflowInstance.GetState()` + `StartWorkflow` pattern. Model the new test after the "ScriptTask fails → workflow state reflects failure" scenario. If no suitable file exists for scope-level error recovery, create `EventSubProcessErrorTests.cs` alongside the existing activity tests.

- [ ] **Step 2: Write the test**

Pattern (adapt to the project's TestClusterFixture / WorkflowTestBase):

```csharp
[TestMethod]
public async Task ErrorEventSubProcess_CatchesScriptTaskFailure_AndCompletesWorkflow()
{
    // Definition:
    //   start -> failingTask (throws) -> end
    //   + EventSubProcess "evtSub1" containing ErrorStartEvent("500") -> handlerTask -> errEnd
    var definition = new WorkflowDefinition
    {
        WorkflowId = "test.event-subprocess-error",
        Activities =
        [
            new StartEvent("start"),
            new ScriptTask("failingTask",
                "throw new System.Exception(\"boom\");"),
            new EndEvent("end"),
            new EventSubProcess("evtSub1")
            {
                Activities =
                [
                    new ErrorStartEvent("errStart", "500"),
                    new ScriptTask("handlerTask",
                        "dynamic v = new System.Dynamic.ExpandoObject(); v.handled = true; return v;"),
                    new EndEvent("errEnd"),
                ],
                SequenceFlows =
                [
                    new SequenceFlow("esf1",
                        /* wired in test fixture — use named references */ null!, null!),
                    new SequenceFlow("esf2", null!, null!),
                ],
                IsInterrupting = true,
            },
        ],
        SequenceFlows = [ /* start -> failingTask -> end */ ],
    };
    // NOTE: the test must wire sequence-flow source/target activity references
    // consistently (the in-memory builder used by other tests does this already —
    // follow the same helper).

    var workflowInstance = await StartAsync(definition);
    await workflowInstance.WaitForTerminalAsync(TimeSpan.FromSeconds(5));

    var state = await workflowInstance.GetState();
    // failingTask is Failed
    Assert.IsTrue(state.CompletedActivities.Any(a => a.ActivityId == "failingTask" && a.ErrorCode == 500));
    // handlerTask ran inside the event sub-process
    Assert.IsTrue(state.CompletedActivities.Any(a => a.ActivityId == "handlerTask"));
    // EventSubProcess host completed
    Assert.IsTrue(state.CompletedActivities.Any(a => a.ActivityId == "evtSub1"));
    // Workflow terminal
    Assert.IsFalse(state.GetActiveActivities().Any());
    // The handler's variable mutation MUST NOT have merged into the root variables
    // (event sub-process scope is isolated)
    Assert.IsFalse(state.GetVariables().Any(kv => kv.Key == "handled"),
        "Event sub-process variable mutations must stay isolated from parent scope");
}
```

**Before running**: verify the exact helper names (`StartAsync`, `WaitForTerminalAsync`, `GetState`, `CompletedActivities`, `GetVariables`) by reading `ScriptTaskTests.cs` and the `WorkflowTestBase`. Rename as needed.

- [ ] **Step 3: Run the integration test**

```
dotnet test Fleans.Application.Tests --filter FullyQualifiedName~EventSubProcessErrorTests
```
Expected: PASS.

- [ ] **Step 4: Run full test suite**

```
dotnet test
```
Expected: all tests PASS.

- [ ] **Step 5: Commit**

```
git add src/Fleans/Fleans.Application.Tests/EventSubProcessErrorTests.cs
git commit -m "test(application): error event sub-process integration test via TestCluster"
```

---

## Task 5: Manual test plan + BPMN fixture

**Files:**
- Create: `tests/manual/19-event-subprocess-error/error-event-subprocess.bpmn`
- Create: `tests/manual/19-event-subprocess-error/test-plan.md`

- [ ] **Step 1: Create the BPMN fixture**

Follow the BPMN Fixture Authoring Rules in `CLAUDE.md`:
- Use `<scriptTask scriptFormat="csharp">` (never bare `<task>`).
- Include `<bpmndi:BPMNDiagram>` section.
- No time-based delays needed for this fixture.

`tests/manual/19-event-subprocess-error/error-event-subprocess.bpmn`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
             xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
             xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
             xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
             id="EventSubProcessErrorDefinitions"
             targetNamespace="http://fleans/tests">
  <process id="evtSubErrorProcess" isExecutable="true">
    <startEvent id="start"/>
    <scriptTask id="failingTask" scriptFormat="csharp">
      <script>throw new System.Exception("boom");</script>
    </scriptTask>
    <endEvent id="normalEnd"/>

    <sequenceFlow id="sf1" sourceRef="start" targetRef="failingTask"/>
    <sequenceFlow id="sf2" sourceRef="failingTask" targetRef="normalEnd"/>

    <subProcess id="errorEventSub" triggeredByEvent="true">
      <startEvent id="errStart">
        <errorEventDefinition errorRef="Err500"/>
      </startEvent>
      <scriptTask id="handlerTask" scriptFormat="csharp">
        <script>
          dynamic v = new System.Dynamic.ExpandoObject();
          v.handled = true;
          return v;
        </script>
      </scriptTask>
      <endEvent id="handlerEnd"/>
      <sequenceFlow id="esf1" sourceRef="errStart" targetRef="handlerTask"/>
      <sequenceFlow id="esf2" sourceRef="handlerTask" targetRef="handlerEnd"/>
    </subProcess>
  </process>

  <error id="Err500" errorCode="500"/>

  <bpmndi:BPMNDiagram id="BPMNDiagram_1">
    <bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="evtSubErrorProcess">
      <bpmndi:BPMNShape id="start_di" bpmnElement="start">
        <dc:Bounds x="100" y="100" width="36" height="36"/>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="failingTask_di" bpmnElement="failingTask">
        <dc:Bounds x="200" y="80" width="100" height="80"/>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="normalEnd_di" bpmnElement="normalEnd">
        <dc:Bounds x="360" y="100" width="36" height="36"/>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="errorEventSub_di" bpmnElement="errorEventSub" isExpanded="true">
        <dc:Bounds x="100" y="220" width="340" height="160"/>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="errStart_di" bpmnElement="errStart">
        <dc:Bounds x="130" y="280" width="36" height="36"/>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="handlerTask_di" bpmnElement="handlerTask">
        <dc:Bounds x="220" y="258" width="100" height="80"/>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="handlerEnd_di" bpmnElement="handlerEnd">
        <dc:Bounds x="370" y="280" width="36" height="36"/>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNEdge id="sf1_di" bpmnElement="sf1">
        <di:waypoint x="136" y="118"/>
        <di:waypoint x="200" y="118"/>
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id="sf2_di" bpmnElement="sf2">
        <di:waypoint x="300" y="118"/>
        <di:waypoint x="360" y="118"/>
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id="esf1_di" bpmnElement="esf1">
        <di:waypoint x="166" y="298"/>
        <di:waypoint x="220" y="298"/>
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id="esf2_di" bpmnElement="esf2">
        <di:waypoint x="320" y="298"/>
        <di:waypoint x="370" y="298"/>
      </bpmndi:BPMNEdge>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</definitions>
```

- [ ] **Step 2: Create the test plan**

`tests/manual/19-event-subprocess-error/test-plan.md`:

```markdown
# Manual Test 19 — Event Sub-Process (Error, Interrupting)

## Scenario
A process runs a script task that throws an unhandled exception (error code 500).
The enclosing scope contains an interrupting error event sub-process that catches
the error and runs a handler script task. The handler should execute, the failing
task should be marked failed, and the workflow should terminate via the
event-sub-process path (the normal `end` event is NOT reached).

## Prerequisites
- Aspire host running (`dotnet run --project Fleans.Aspire`)
- `error-event-subprocess.bpmn` available in this folder

## Steps
1. **Deploy** the BPMN: upload `error-event-subprocess.bpmn` via the Web UI (Fleans.Web → Deployments → Upload) or
   `POST https://localhost:7140/Workflow/upload-bpmn` with the file.
2. **Start an instance**:
   `POST https://localhost:7140/Workflow/start` with body
   `{"WorkflowId":"evtSubErrorProcess"}`
3. Open the instance in the Web UI and confirm the running state.
4. Wait briefly (script task throws synchronously).
5. **Verify**:
   - [ ] `failingTask` activity is in **Failed** state with error code `500`.
   - [ ] `errorEventSub` event sub-process host shows as **Completed**.
   - [ ] `handlerTask` shows as **Completed** inside the event sub-process scope.
   - [ ] `handlerEnd` event fired.
   - [ ] Workflow instance state is **Completed** (terminal).
   - [ ] The normal `normalEnd` event is **NOT** reached (no `ActivityCompleted` for it).
   - [ ] Root workflow variables do **NOT** contain `handled=true`
         (event sub-process variable scope is isolated).

## Expected Outcomes Checklist
- [ ] Failing task surfaces error code 500
- [ ] Error event sub-process fires and runs handler
- [ ] Sibling `normalEnd` never executes
- [ ] Workflow completes via event-sub-process path
- [ ] Variable isolation confirmed (no `handled` in root vars)
```

- [ ] **Step 3: Commit**

```
git add tests/manual/19-event-subprocess-error/
git commit -m "test(manual): NN-19 event sub-process error manual test plan"
```

---

## Task 6: Documentation + README update

**Files:**
- Modify: `website/src/content/docs/concepts/bpmn-support.md` (or equivalent).
- Modify: `README.md` BPMN elements table — mark `EventSubProcess (error, interrupting)` as supported.

- [ ] **Step 1: Update website docs**

Read `website/src/content/docs/concepts/bpmn-support.md`. Add a brief subsection under the SubProcess entry describing the interrupting error event sub-process (just error trigger for now; note that message/signal/timer triggers and non-interrupting variants land in subsequent slices). Include the minimal XML example from the fixture above.

- [ ] **Step 2: Update README BPMN elements table**

Find the BPMN elements table in `README.md`. Add a row (or update an existing `EventSubProcess` row if one exists) indicating:
- `EventSubProcess (error, interrupting)` — ✅ Supported (Slice B)
- Leave timer/message/signal/non-interrupting rows as "Pending (#265/#266/#267/#268)".

- [ ] **Step 3: Build the website to verify**

```
cd website && npm install && npm run build && cd ..
```
Expected: build succeeds.

- [ ] **Step 4: Commit**

```
git add website/src/content/docs/concepts/bpmn-support.md README.md
git commit -m "docs: event sub-process error (interrupting) support"
```

---

## Task 7: Final verification + PR

- [ ] **Step 1: Full build + test**

```
cd src/Fleans && dotnet build && dotnet test && cd ../..
```
Expected: all projects build; all tests PASS.

- [ ] **Step 2: Push branch**

```
git push -u origin feature/event-subprocess-error
```

- [ ] **Step 3: Open PR**

Use the `/create-pr` or `gh pr create` workflow:
- Title: `feat(domain): event sub-process error (interrupting) — slice #B (#264)`
- Body: summary of the slice, link to #264 and #227, list of tests added, note that peer listener deregistration is a TODO for slices #C–#E.

- [ ] **Step 4: Verify CI green, request review**

---

## Self-Review Checklist (run before marking plan complete)

- [x] Every task has at least one test.
- [x] All code blocks in the plan are complete (no `// ...` placeholders except for documented "unchanged existing code" regions in `FailActivity` which are intentionally left in place).
- [x] Types and method names are consistent across tasks (`FindErrorEventSubProcessHandler`, `TryActivateErrorEventSubProcess`, `EventSubProcess`, `ErrorStartEvent`).
- [x] Precedence rule (boundary > event-sub) is tested.
- [x] Interrupting cancellation of long-running sibling (user task) is tested.
- [x] Nested-scope bubble-up is tested.
- [x] Variable scope isolation (no merge-back) is specified and tested at integration level.
- [x] Manual test plan + fixture included per CLAUDE.md.
- [x] README + website docs updates included per CLAUDE.md "Documentation rule".
- [x] No changes to `WorkflowInstance` grain partial files — all logic lives in `WorkflowExecution` aggregate (slice-appropriate scope).
