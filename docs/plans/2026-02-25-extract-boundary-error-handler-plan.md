# Extract Boundary Error Handler Query — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extract the "find matching BoundaryErrorEvent" domain query from `WorkflowInstance.FailActivityWithBoundaryCheck` into `IWorkflowDefinition.FindBoundaryErrorHandler`, fixing two pre-existing bugs (wrong scope + non-deterministic priority).

**Architecture:** Default interface method on `IWorkflowDefinition` walks the definition hierarchy (using existing `FindScopeForActivity`) to find a matching `BoundaryErrorEvent`. The grain method simplifies to: call domain method → cancel intermediate scopes if bubbled → invoke handler.

**Tech Stack:** .NET 10, C# 14, MSTest, Orleans

---

### Task 1: Add FindBoundaryErrorHandler to IWorkflowDefinition with tests

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Definitions/Workflow.cs`
- Create: `src/Fleans/Fleans.Domain.Tests/WorkflowDefinitionBoundaryErrorTests.cs`

**Step 1: Write the failing tests**

Create `src/Fleans/Fleans.Domain.Tests/WorkflowDefinitionBoundaryErrorTests.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowDefinitionBoundaryErrorTests
{
    [TestMethod]
    public void FindBoundaryErrorHandler_DirectMatch_ReturnsBoundaryEvent()
    {
        // Arrange
        var task1 = new TaskActivity("task1");
        var boundary = new BoundaryErrorEvent("boundary1", "task1", "500");
        var endEvent = new EndEvent("end1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, boundary, endEvent],
            SequenceFlows = [new SequenceFlow(boundary, endEvent)]
        };

        // Act
        var result = definition.FindBoundaryErrorHandler("task1", "500");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("boundary1", result.Value.BoundaryEvent.ActivityId);
        Assert.AreEqual("task1", result.Value.AttachedToActivityId);
        Assert.AreSame(definition, result.Value.Scope);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_NoMatch_ReturnsNull()
    {
        // Arrange
        var task1 = new TaskActivity("task1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1],
            SequenceFlows = []
        };

        // Act
        var result = definition.FindBoundaryErrorHandler("task1", "500");

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_CatchAll_MatchesAnyErrorCode()
    {
        // Arrange
        var task1 = new TaskActivity("task1");
        var boundary = new BoundaryErrorEvent("boundary1", "task1", null); // catch-all
        var endEvent = new EndEvent("end1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, boundary, endEvent],
            SequenceFlows = [new SequenceFlow(boundary, endEvent)]
        };

        // Act
        var result = definition.FindBoundaryErrorHandler("task1", "999");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("boundary1", result.Value.BoundaryEvent.ActivityId);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_SpecificCodePrioritizedOverCatchAll()
    {
        // Arrange
        var task1 = new TaskActivity("task1");
        var catchAll = new BoundaryErrorEvent("catchAll", "task1", null);
        var specific = new BoundaryErrorEvent("specific", "task1", "500");
        var endEvent = new EndEvent("end1");
        // catchAll listed BEFORE specific — without priority logic, FirstOrDefault would pick catchAll
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, catchAll, specific, endEvent],
            SequenceFlows = [new SequenceFlow(catchAll, endEvent), new SequenceFlow(specific, endEvent)]
        };

        // Act
        var result = definition.FindBoundaryErrorHandler("task1", "500");

        // Assert — specific match wins over catch-all regardless of ordering
        Assert.IsNotNull(result);
        Assert.AreEqual("specific", result.Value.BoundaryEvent.ActivityId);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_CodeMismatch_ReturnsNull()
    {
        // Arrange
        var task1 = new TaskActivity("task1");
        var boundary = new BoundaryErrorEvent("boundary1", "task1", "404");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [task1, boundary],
            SequenceFlows = []
        };

        // Act
        var result = definition.FindBoundaryErrorHandler("task1", "500");

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_BubbleUpThroughSubProcess()
    {
        // Arrange — task1 inside sub1, boundary attached to sub1 in root scope
        var task1 = new TaskActivity("task1");
        var startEvent = new StartEvent("start1");
        var sub1 = new SubProcess("sub1")
        {
            Activities = [startEvent, task1],
            SequenceFlows = [new SequenceFlow(startEvent, task1)]
        };
        var boundary = new BoundaryErrorEvent("boundary1", "sub1", "500");
        var endEvent = new EndEvent("end1");
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [sub1, boundary, endEvent],
            SequenceFlows = [new SequenceFlow(boundary, endEvent)]
        };

        // Act
        var result = definition.FindBoundaryErrorHandler("task1", "500");

        // Assert — bubbled up: boundary is on sub1, not task1
        Assert.IsNotNull(result);
        Assert.AreEqual("boundary1", result.Value.BoundaryEvent.ActivityId);
        Assert.AreEqual("sub1", result.Value.AttachedToActivityId);
        Assert.AreSame(definition, result.Value.Scope);
    }

    [TestMethod]
    public void FindBoundaryErrorHandler_DirectMatchInsideSubProcess()
    {
        // Arrange — task1 inside sub1, boundary attached to task1 inside sub1
        var task1 = new TaskActivity("task1");
        var startEvent = new StartEvent("start1");
        var boundary = new BoundaryErrorEvent("boundary1", "task1", "500");
        var endEvent = new EndEvent("end1");
        var sub1 = new SubProcess("sub1")
        {
            Activities = [startEvent, task1, boundary, endEvent],
            SequenceFlows = [new SequenceFlow(startEvent, task1), new SequenceFlow(boundary, endEvent)]
        };
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [sub1],
            SequenceFlows = []
        };

        // Act
        var result = definition.FindBoundaryErrorHandler("task1", "500");

        // Assert — direct match inside SubProcess, scope is the SubProcess
        Assert.IsNotNull(result);
        Assert.AreEqual("boundary1", result.Value.BoundaryEvent.ActivityId);
        Assert.AreEqual("task1", result.Value.AttachedToActivityId);
        Assert.AreSame(sub1, result.Value.Scope);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~WorkflowDefinitionBoundaryErrorTests" --verbosity quiet 2>&1 | tail -5`

Expected: Build error — `FindBoundaryErrorHandler` doesn't exist on `IWorkflowDefinition` yet.

**Step 3: Add the method to IWorkflowDefinition**

In `src/Fleans/Fleans.Domain/Definitions/Workflow.cs`, add to `IWorkflowDefinition` after `GetEventBasedGatewaySiblings`:

```csharp
/// <summary>
/// Finds the matching BoundaryErrorEvent for a failed activity, searching the activity's
/// scope and walking up parent SubProcess scopes if not found.
/// Specific error code matches take priority over catch-all (null ErrorCode) boundaries.
/// </summary>
(BoundaryErrorEvent BoundaryEvent, IWorkflowDefinition Scope, string AttachedToActivityId)?
    FindBoundaryErrorHandler(string failedActivityId, string? errorCode)
{
    var targetActivityId = failedActivityId;

    while (true)
    {
        var scope = FindScopeForActivity(targetActivityId);
        if (scope is null) return null;

        var candidates = scope.Activities
            .OfType<BoundaryErrorEvent>()
            .Where(b => b.AttachedToActivityId == targetActivityId
                && (b.ErrorCode == null || b.ErrorCode == errorCode))
            .ToList();

        // Prefer specific error code match over catch-all
        var match = candidates.FirstOrDefault(b => b.ErrorCode == errorCode)
                    ?? candidates.FirstOrDefault(b => b.ErrorCode == null);

        if (match is not null)
            return (match, scope, targetActivityId);

        // Bubble up: if scope is a SubProcess, check its parent for boundary on the SubProcess
        if (scope is SubProcess subProcess)
            targetActivityId = subProcess.ActivityId;
        else
            return null; // at root scope, no match found
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~WorkflowDefinitionBoundaryErrorTests" --verbosity quiet 2>&1 | tail -5`

Expected: 7 tests pass.

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Definitions/Workflow.cs src/Fleans/Fleans.Domain.Tests/WorkflowDefinitionBoundaryErrorTests.cs
git commit -m "feat: add FindBoundaryErrorHandler to IWorkflowDefinition with tests

Extracts boundary error event matching from WorkflowInstance to domain.
Fixes: specific error codes now prioritized over catch-all boundaries.
Fixes: scope-aware search works correctly inside SubProcesses."
```

---

### Task 2: Update WorkflowInstance to use FindBoundaryErrorHandler

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`

**Step 1: Rewrite FailActivityWithBoundaryCheck**

Replace the current `FailActivityWithBoundaryCheck` method (lines 632-686) with:

```csharp
private async Task FailActivityWithBoundaryCheck(string activityId, Exception exception)
{
    await FailActivityState(activityId, exception);

    var definition = await GetWorkflowDefinition();
    var activityEntry = State.GetFirstActive(activityId) ?? State.Entries.Last(e => e.ActivityId == activityId);
    var activityGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(activityEntry.ActivityInstanceId);
    var errorState = await activityGrain.GetErrorState();

    if (errorState is null)
    {
        await ExecuteWorkflow();
        return;
    }

    var match = definition.FindBoundaryErrorHandler(activityId, errorState.Code.ToString());
    if (match is null)
    {
        await ExecuteWorkflow();
        return;
    }

    var (boundaryEvent, scopeDefinition, attachedToActivityId) = match.Value;

    if (attachedToActivityId == activityId)
    {
        // Direct match — boundary is on the failed activity itself
        await _boundaryHandler.HandleBoundaryErrorAsync(activityId, boundaryEvent, activityEntry.ActivityInstanceId, scopeDefinition);
    }
    else
    {
        // Bubbled up — cancel intermediate scopes between failed activity and matched SubProcess
        var failedEntry = State.Entries.Last(e => e.ActivityId == activityId);
        State.CompleteEntries([failedEntry]);

        var currentScopeId = failedEntry.ScopeId;
        while (currentScopeId.HasValue)
        {
            var scopeEntry = State.Entries.First(e => e.ActivityInstanceId == currentScopeId.Value);

            if (scopeEntry.ActivityId == attachedToActivityId)
            {
                // Found the SubProcess the boundary is attached to — cancel and handle
                await CancelScopeChildren(currentScopeId.Value);
                var scopeInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(scopeEntry.ActivityInstanceId);
                await scopeInstance.Cancel("Sub-process interrupted by error boundary");
                State.CompleteEntries([scopeEntry]);
                await _boundaryHandler.HandleBoundaryErrorAsync(scopeEntry.ActivityId, boundaryEvent, scopeEntry.ActivityInstanceId, scopeDefinition);
                return;
            }

            currentScopeId = scopeEntry.ScopeId;
        }
    }
}
```

**Step 2: Build to verify compilation**

Run: `dotnet build src/Fleans/ --verbosity quiet 2>&1 | tail -5`

Expected: 0 errors.

**Step 3: Run all tests**

Run: `dotnet test src/Fleans/ --verbosity quiet 2>&1 | tail -15`

Expected: All tests pass (358 existing + 7 new = 365).

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "refactor: use FindBoundaryErrorHandler for boundary error matching

Replace inline LINQ + scope walking in FailActivityWithBoundaryCheck
with domain method. Fixes wrong scope definition passed in direct match
case (was passing root instead of activity scope)."
```
