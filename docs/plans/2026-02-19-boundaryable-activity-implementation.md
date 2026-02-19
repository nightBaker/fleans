# Boundaryable Activity Refactoring Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extract boundary event registration from `Activity.ExecuteAsync` into a `BoundarableActivity` base record, called by `WorkflowInstance` after execution.

**Architecture:** New `IBoundarableActivity` interface + `BoundarableActivity` abstract record sits between `Activity` and `TaskActivity`/`CallActivity`. `WorkflowInstance.ExecuteWorkflow()` checks for the interface after `ExecuteAsync` and calls `RegisterBoundaryEventsAsync`.

**Tech Stack:** C# / .NET / Orleans / MSTest / NSubstitute

---

### Task 1: Create IBoundarableActivity interface, BoundarableActivity record, and update hierarchy

**Files:**
- Create: `src/Fleans/Fleans.Domain/IBoundarableActivity.cs`
- Create: `src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/TaskActivity.cs:8` — change base from `Activity` to `BoundarableActivity`
- Modify: `src/Fleans/Fleans.Domain/Activities/CallActivity.cs:10` — change base from `Activity` to `BoundarableActivity`

**Step 1: Create the interface**

Create `src/Fleans/Fleans.Domain/IBoundarableActivity.cs`:

```csharp
namespace Fleans.Domain;

public interface IBoundarableActivity
{
    Task RegisterBoundaryEventsAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext);
}
```

**Step 2: Create the abstract record**

Create `src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs`:

```csharp
using Orleans;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record BoundarableActivity(string ActivityId)
    : Activity(ActivityId), IBoundarableActivity
{
    public async Task RegisterBoundaryEventsAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var hostInstanceId = await activityContext.GetActivityInstanceId();

        foreach (var boundaryTimer in definition.Activities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterTimerReminder(hostInstanceId, boundaryTimer.ActivityId, boundaryTimer.TimerDefinition.GetDueTime());
        }

        foreach (var boundaryMsg in definition.Activities.OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterBoundaryMessageSubscription(hostInstanceId, boundaryMsg.ActivityId, boundaryMsg.MessageDefinitionId);
        }
    }
}
```

**Step 3: Update TaskActivity**

In `src/Fleans/Fleans.Domain/Activities/TaskActivity.cs`, change line 8:

```csharp
// Before:
public record TaskActivity(string ActivityId) : Activity(ActivityId)
// After:
public record TaskActivity(string ActivityId) : BoundarableActivity(ActivityId)
```

**Step 4: Update CallActivity**

In `src/Fleans/Fleans.Domain/Activities/CallActivity.cs`, change line 10:

```csharp
// Before:
    [property: Id(5)] bool PropagateAllChildVariables = true) : Activity(ActivityId)
// After:
    [property: Id(5)] bool PropagateAllChildVariables = true) : BoundarableActivity(ActivityId)
```

**Step 5: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS

**Step 6: Run all tests to verify no regressions**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS

**Step 7: Commit**

```bash
git add src/Fleans/Fleans.Domain/IBoundarableActivity.cs src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs src/Fleans/Fleans.Domain/Activities/TaskActivity.cs src/Fleans/Fleans.Domain/Activities/CallActivity.cs
git commit -m "feat: add IBoundarableActivity and BoundarableActivity, update TaskActivity and CallActivity hierarchy"
```

---

### Task 2: Add RegisterTimerReminder stub to ActivityTestHelper and write tests

**Files:**
- Modify: `src/Fleans/Fleans.Domain.Tests/ActivityTestHelper.cs:30` — add `RegisterTimerReminder` stub
- Create: `src/Fleans/Fleans.Domain.Tests/BoundarableActivityTests.cs`

**Step 1: Add missing RegisterTimerReminder stub**

In `src/Fleans/Fleans.Domain.Tests/ActivityTestHelper.cs`, after line 30 (`context.RegisterBoundaryMessageSubscription(...)`), add:

```csharp
        context.RegisterTimerReminder(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(ValueTask.CompletedTask);
```

**Step 2: Write the test file**

Create `src/Fleans/Fleans.Domain.Tests/BoundarableActivityTests.cs`:

```csharp
using Fleans.Domain.Activities;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class BoundarableActivityTests
{
    [TestMethod]
    public async Task RegisterBoundaryEventsAsync_ShouldRegisterTimerReminder_WhenBoundaryTimerAttached()
    {
        // Arrange
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task, boundaryTimer], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var activityInstanceId = Guid.NewGuid();
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1", activityInstanceId);

        // Act
        await task.RegisterBoundaryEventsAsync(workflowContext, activityContext);

        // Assert
        await workflowContext.Received(1).RegisterTimerReminder(
            activityInstanceId, "bt1", TimeSpan.FromMinutes(10));
    }

    [TestMethod]
    public async Task RegisterBoundaryEventsAsync_ShouldRegisterMessageSubscription_WhenMessageBoundaryAttached()
    {
        // Arrange
        var task = new TaskActivity("task1");
        var boundaryMsg = new MessageBoundaryEvent("bm1", "task1", "msg-def-1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task, boundaryMsg], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var activityInstanceId = Guid.NewGuid();
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1", activityInstanceId);

        // Act
        await task.RegisterBoundaryEventsAsync(workflowContext, activityContext);

        // Assert
        await workflowContext.Received(1).RegisterBoundaryMessageSubscription(
            activityInstanceId, "bm1", "msg-def-1");
    }

    [TestMethod]
    public async Task RegisterBoundaryEventsAsync_ShouldNotRegister_WhenNoBoundariesAttached()
    {
        // Arrange
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition([task], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1");

        // Act
        await task.RegisterBoundaryEventsAsync(workflowContext, activityContext);

        // Assert
        await workflowContext.DidNotReceive().RegisterTimerReminder(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<TimeSpan>());
        await workflowContext.DidNotReceive().RegisterBoundaryMessageSubscription(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [TestMethod]
    public async Task RegisterBoundaryEventsAsync_ShouldOnlyRegisterMatchingBoundaries()
    {
        // Arrange
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var boundaryOnTask1 = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var boundaryOnTask2 = new BoundaryTimerEvent("bt2", "task2", timerDef);
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [task1, task2, boundaryOnTask1, boundaryOnTask2], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("task1");

        // Act
        await task1.RegisterBoundaryEventsAsync(workflowContext, activityContext);

        // Assert
        await workflowContext.Received(1).RegisterTimerReminder(
            Arg.Any<Guid>(), "bt1", Arg.Any<TimeSpan>());
        await workflowContext.DidNotReceive().RegisterTimerReminder(
            Arg.Any<Guid>(), "bt2", Arg.Any<TimeSpan>());
    }

    [TestMethod]
    public async Task RegisterBoundaryEventsAsync_CallActivity_ShouldRegisterBoundaryTimer()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "sub-process", [], []);
        var timerDef = new TimerDefinition(TimerType.Duration, "PT15M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "call1", timerDef);
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [callActivity, boundaryTimer], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var activityInstanceId = Guid.NewGuid();
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("call1", activityInstanceId);

        // Act
        await callActivity.RegisterBoundaryEventsAsync(workflowContext, activityContext);

        // Assert
        await workflowContext.Received(1).RegisterTimerReminder(
            activityInstanceId, "bt1", TimeSpan.FromMinutes(15));
    }
}
```

**Step 3: Run tests to verify they pass**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~BoundarableActivityTests"`
Expected: 5 PASSED

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain.Tests/ActivityTestHelper.cs src/Fleans/Fleans.Domain.Tests/BoundarableActivityTests.cs
git commit -m "test: add unit tests for BoundarableActivity.RegisterBoundaryEventsAsync"
```

---

### Task 3: Remove boundary registration from Activity.ExecuteAsync and add call in WorkflowInstance

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/Activity.cs:22-34` — remove boundary registration code
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs:56` — add IBoundarableActivity check after ExecuteAsync

**Step 1: Remove boundary registration code from Activity.ExecuteAsync**

In `src/Fleans/Fleans.Domain/Activities/Activity.cs`, remove lines 22-34 (the comment and both foreach loops). The method becomes:

```csharp
internal virtual async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
{
    var definition = await workflowContext.GetWorkflowDefinition();
    await activityContext.Execute();
    await activityContext.PublishEvent(new WorkflowActivityExecutedEvent(await workflowContext.GetWorkflowInstanceId(),
        definition.WorkflowId,
        await activityContext.GetActivityInstanceId(),
        ActivityId,
        GetType().Name));
}
```

**Step 2: Add boundary registration call in WorkflowInstance.ExecuteWorkflow**

In `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`, after line 56 (`await currentActivity.ExecuteAsync(this, activityState);`), add the IBoundarableActivity check:

```csharp
                await currentActivity.ExecuteAsync(this, activityState);

                if (currentActivity is IBoundarableActivity boundarable)
                {
                    await boundarable.RegisterBoundaryEventsAsync(this, activityState);
                }
```

Note: `using Fleans.Domain;` should already be present at the top of the file.

**Step 3: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS

**Step 4: Run full test suite**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS — boundary timer and message boundary integration tests should still pass since the registration now happens from WorkflowInstance instead of inside Activity.ExecuteAsync

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/Activity.cs src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "refactor: move boundary registration from Activity.ExecuteAsync to WorkflowInstance"
```

---

### Task 4: Final verification

**Step 1: Run full test suite**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS

**Step 2: Verify the refactoring is clean**

Check that:
- `Activity.ExecuteAsync` no longer references `BoundaryTimerEvent` or `MessageBoundaryEvent`
- `TaskActivity` and `CallActivity` inherit `BoundarableActivity`
- `ScriptTask` still inherits `TaskActivity` (unchanged)
- `WorkflowInstance.ExecuteWorkflow()` has the `IBoundarableActivity` check
- All existing boundary timer and message boundary tests still pass
