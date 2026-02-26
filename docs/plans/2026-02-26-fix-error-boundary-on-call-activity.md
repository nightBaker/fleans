# Fix: Child Process Errors Don't Propagate to Parent Error Boundary

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** When a child workflow fails, propagate the error to the parent's CallActivity so its error boundary event fires.

**Architecture:** Add parent notification in the "no boundary handler" branch of `FailActivityWithBoundaryCheck`. The `OnChildWorkflowFailed` method already exists — just needs to be called.

**Tech Stack:** C# / Orleans / MSTest

---

### Task 1: Write the failing test

**Files:**
- Modify: `src/Fleans/Fleans.Application.Tests/CallActivityTests.cs`

**Step 1: Write the test**

Add this test to `CallActivityTests.cs` after `CallActivity_BoundaryErrorEvent_ShouldRouteToRecoveryPath`:

```csharp
[TestMethod]
public async Task CallActivity_ChildFails_ShouldPropagateErrorToParentBoundary()
{
    // Arrange — deploy child workflow: start → childTask → end
    var childStart = new StartEvent("childStart");
    var childTask = new TaskActivity("childTask");
    var childEnd = new EndEvent("childEnd");

    var childWorkflow = new WorkflowDefinition
    {
        WorkflowId = "childThatFails",
        Activities = [childStart, childTask, childEnd],
        SequenceFlows =
        [
            new SequenceFlow("cs1", childStart, childTask),
            new SequenceFlow("cs2", childTask, childEnd)
        ]
    };

    var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
    await factory.DeployWorkflow(childWorkflow, "<xml/>");

    // Arrange — parent workflow: start → callFailing → happyEnd
    //   with errorBoundary on callFailing → errorHandler → end2
    var parentStart = new StartEvent("start");
    var callFailing = new CallActivity("callFailing", "childThatFails", [], []);
    var happyEnd = new EndEvent("happyEnd");
    var errorBoundary = new BoundaryErrorEvent("errorBoundary", "callFailing", null);
    var errorHandler = new TaskActivity("errorHandler");
    var end2 = new EndEvent("end2");

    var parentWorkflow = new WorkflowDefinition
    {
        WorkflowId = "errorBoundaryTest",
        Activities = [parentStart, callFailing, happyEnd, errorBoundary, errorHandler, end2],
        SequenceFlows =
        [
            new SequenceFlow("ps1", parentStart, callFailing),
            new SequenceFlow("ps2", callFailing, happyEnd),
            new SequenceFlow("ps3", errorBoundary, errorHandler),
            new SequenceFlow("ps4", errorHandler, end2)
        ]
    };

    await factory.DeployWorkflow(parentWorkflow, "<xml/>");

    var parentInstance = await factory.CreateWorkflowInstanceGrain("errorBoundaryTest");
    var parentInstanceId = parentInstance.GetPrimaryKey();

    // Act — start parent (spawns child), then fail the child's task
    await parentInstance.StartWorkflow();

    var parentSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
    Assert.IsNotNull(parentSnapshot);
    var callActivitySnapshot = parentSnapshot.ActiveActivities.First(a => a.ActivityId == "callFailing");
    Assert.IsNotNull(callActivitySnapshot.ChildWorkflowInstanceId, "Child workflow should have been spawned");
    var childInstanceId = callActivitySnapshot.ChildWorkflowInstanceId.Value;

    var childInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(childInstanceId);
    await childInstance.FailActivity("childTask", new Exception("Something went wrong"));

    // Assert — error boundary should have fired, routing to errorHandler
    var finalSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
    Assert.IsNotNull(finalSnapshot);

    Assert.IsTrue(
        finalSnapshot.ActiveActivities.Any(a => a.ActivityId == "errorHandler"),
        "Error handler should be active after child failure propagates");

    Assert.IsTrue(
        finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "errorBoundary"),
        "Error boundary event should be completed");

    // Complete the error handler to finish the parent
    await parentInstance.CompleteActivity("errorHandler", new ExpandoObject());

    var completedSnapshot = await QueryService.GetStateSnapshot(parentInstanceId);
    Assert.IsNotNull(completedSnapshot);
    Assert.IsTrue(completedSnapshot.IsCompleted, "Parent should be completed after error handler finishes");
    CollectionAssert.Contains(completedSnapshot.CompletedActivityIds, "end2");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Fleans/ --filter "CallActivity_ChildFails_ShouldPropagateErrorToParentBoundary" --no-build`

First build: `dotnet build src/Fleans/`

Then: `dotnet test src/Fleans/ --filter "CallActivity_ChildFails_ShouldPropagateErrorToParentBoundary"`

Expected: FAIL — the child fails but never notifies the parent, so errorHandler is never activated.

---

### Task 2: Implement the fix

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs` (lines 648-655)

**Step 1: Add parent notification in the "no boundary handler" branch**

In `FailActivityWithBoundaryCheck`, change the `match is null` branch from:

```csharp
if (match is null)
{
    // No boundary handler — complete the failed entry so the parent scope stays active
    // (its executing SubProcess grain prevents ExecuteWorkflow from auto-completing)
    var failedEntry = State.Entries.Last(e => e.ActivityId == activityId);
    State.CompleteEntries([failedEntry]);
    await ExecuteWorkflow();
    return;
}
```

To:

```csharp
if (match is null)
{
    // No boundary handler — complete the failed entry so the parent scope stays active
    // (its executing SubProcess grain prevents ExecuteWorkflow from auto-completing)
    var failedEntry = State.Entries.Last(e => e.ActivityId == activityId);
    State.CompleteEntries([failedEntry]);
    await ExecuteWorkflow();

    // If this is a child workflow with no remaining active activities,
    // propagate the failure to the parent (mirrors Complete() success path)
    if (State.ParentWorkflowInstanceId.HasValue && !State.GetActiveActivities().Any())
    {
        var parent = _grainFactory.GetGrain<IWorkflowInstanceGrain>(State.ParentWorkflowInstanceId.Value);
        await parent.OnChildWorkflowFailed(State.ParentActivityId!, exception);
    }

    return;
}
```

**Step 2: Add a log message for the parent notification**

Add a `[LoggerMessage]` for child failure propagation near the other child-workflow log methods:

```csharp
[LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Child workflow failed with no boundary handler, propagating error to parent. ParentActivityId={ParentActivityId}")]
private partial void LogChildFailurePropagatedToParent(string parentActivityId);
```

Call it before the `OnChildWorkflowFailed` call.

**Step 3: Run the test to verify it passes**

Run: `dotnet test src/Fleans/ --filter "CallActivity_ChildFails_ShouldPropagateErrorToParentBoundary"`

Expected: PASS

**Step 4: Run all tests to verify no regressions**

Run: `dotnet test src/Fleans/`

Expected: All tests pass.

---

### Task 3: Commit

**Step 1: Commit the fix**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs src/Fleans/Fleans.Application.Tests/CallActivityTests.cs
git commit -m "fix: propagate child workflow errors to parent error boundary

When a child workflow fails and has no local error boundary handler,
notify the parent via OnChildWorkflowFailed so the parent's error
boundary on the CallActivity fires correctly.

Fixes manual test 11."
```

---

### Task 4: Update manual test results

**Files:**
- Modify: `docs/plans/2026-02-25-manual-test-results.md`

**Step 1: Mark Bug 2 as fixed**

Update the test 11 row in the results table from `**BUG**` to `PASSED (fixed)`.

Add a note to Bug 2 section: `**Status:** Fixed in commit <hash>`

**Step 2: Commit**

```bash
git add docs/plans/2026-02-25-manual-test-results.md
git commit -m "docs: mark error boundary bug as fixed in manual test results"
```
