# Non-Interrupting Boundary Events Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add non-interrupting boundary event support so boundary timer/message/signal events can fire without cancelling the attached activity, plus timer cycle support for repeating non-interrupting timers.

**Architecture:** Add `IsInterrupting` flag (default `true`) to boundary event records. `BoundaryEventHandler` conditionally skips cancellation when `IsInterrupting == false`. Non-interrupting boundaries clone the variable scope and spawn a parallel branch. Timer cycle support re-registers the timer grain after each fire with decremented repeat count.

**Tech Stack:** C# 14, .NET 10, Orleans 9.2, MSTest, NSubstitute

**Design doc:** `docs/plans/2026-03-05-non-interrupting-boundaries-design.md`

---

### Task 1: Add IsInterrupting to BoundaryTimerEvent

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/BoundaryTimerEvent.cs`
- Test: `src/Fleans/Fleans.Domain.Tests/BoundaryTimerEventDomainTests.cs`

**Step 1: Write failing test**

Add test to `BoundaryTimerEventDomainTests.cs`:

```csharp
[TestMethod]
public void BoundaryTimerEvent_IsInterrupting_DefaultsToTrue()
{
    var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
    var boundary = new BoundaryTimerEvent("bt1", "task1", timerDef);
    Assert.IsTrue(boundary.IsInterrupting);
}

[TestMethod]
public void BoundaryTimerEvent_IsInterrupting_CanBeSetToFalse()
{
    var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
    var boundary = new BoundaryTimerEvent("bt1", "task1", timerDef, IsInterrupting: false);
    Assert.IsFalse(boundary.IsInterrupting);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test --filter "BoundaryTimerEvent_IsInterrupting" --no-build` from `src/Fleans/`
Expected: Build error — `IsInterrupting` parameter does not exist

**Step 3: Write minimal implementation**

In `BoundaryTimerEvent.cs`, change the record to:

```csharp
[GenerateSerializer]
public record BoundaryTimerEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] TimerDefinition TimerDefinition,
    [property: Id(3)] bool IsInterrupting = true) : Activity(ActivityId)
```

Remove the TODO comment about non-interrupting support.

**Step 4: Build and run test to verify it passes**

Run: `dotnet build && dotnet test --filter "BoundaryTimerEvent_IsInterrupting"` from `src/Fleans/`
Expected: PASS (2 tests)

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/BoundaryTimerEvent.cs src/Fleans/Fleans.Domain.Tests/BoundaryTimerEventDomainTests.cs
git commit -m "feat: add IsInterrupting flag to BoundaryTimerEvent"
```

---

### Task 2: Add IsInterrupting to MessageBoundaryEvent and SignalBoundaryEvent

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/MessageBoundaryEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/SignalBoundaryEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/BoundaryErrorEvent.cs`
- Test: `src/Fleans/Fleans.Domain.Tests/MessageBoundaryEventDomainTests.cs`
- Test: `src/Fleans/Fleans.Domain.Tests/SignalBoundaryEventDomainTests.cs`
- Test: `src/Fleans/Fleans.Domain.Tests/BoundaryErrorEventDomainTests.cs`

**Step 1: Write failing tests**

Add to each domain test file the same pattern:

```csharp
// MessageBoundaryEventDomainTests.cs
[TestMethod]
public void MessageBoundaryEvent_IsInterrupting_DefaultsToTrue()
{
    var boundary = new MessageBoundaryEvent("bm1", "task1", "msg1");
    Assert.IsTrue(boundary.IsInterrupting);
}

[TestMethod]
public void MessageBoundaryEvent_IsInterrupting_CanBeSetToFalse()
{
    var boundary = new MessageBoundaryEvent("bm1", "task1", "msg1", IsInterrupting: false);
    Assert.IsFalse(boundary.IsInterrupting);
}
```

Same pattern for `SignalBoundaryEvent` and `BoundaryErrorEvent`.

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "IsInterrupting"` from `src/Fleans/`
Expected: Build errors for missing `IsInterrupting` parameter

**Step 3: Write implementation**

`MessageBoundaryEvent.cs`:
```csharp
[GenerateSerializer]
public record MessageBoundaryEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] string MessageDefinitionId,
    [property: Id(3)] bool IsInterrupting = true) : Activity(ActivityId)
```

`SignalBoundaryEvent.cs`:
```csharp
[GenerateSerializer]
public record SignalBoundaryEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] string SignalDefinitionId,
    [property: Id(3)] bool IsInterrupting = true) : Activity(ActivityId)
```

`BoundaryErrorEvent.cs`:
```csharp
[GenerateSerializer]
public record BoundaryErrorEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] string? ErrorCode,
    [property: Id(3)] bool IsInterrupting = true) : Activity(ActivityId)
```

Remove TODO comments from all three files.

**Step 4: Build and run tests**

Run: `dotnet build && dotnet test --filter "IsInterrupting"` from `src/Fleans/`
Expected: All 8 IsInterrupting tests pass

**Step 5: Run full test suite to verify no regressions**

Run: `dotnet test` from `src/Fleans/`
Expected: All existing tests pass (default `true` preserves existing behavior)

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/MessageBoundaryEvent.cs src/Fleans/Fleans.Domain/Activities/SignalBoundaryEvent.cs src/Fleans/Fleans.Domain/Activities/BoundaryErrorEvent.cs src/Fleans/Fleans.Domain.Tests/
git commit -m "feat: add IsInterrupting flag to all boundary event types"
```

---

### Task 3: Parse cancelActivity attribute in BpmnConverter

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs:364-404`
- Test: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverterTests.cs` (or create if missing)

**Step 1: Write failing test**

Check if `BpmnConverterTests.cs` exists. If not, create it. Add test:

```csharp
[TestMethod]
public async Task ParseBoundaryEvent_NonInterrupting_SetsIsInterruptingFalse()
{
    var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""test"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""task1"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
    </scriptTask>
    <boundaryEvent id=""bt1"" attachedToRef=""task1"" cancelActivity=""false"">
      <timerEventDefinition>
        <timeDuration>PT10S</timeDuration>
      </timerEventDefinition>
    </boundaryEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bt1"" targetRef=""end"" />
  </process>
</definitions>";

    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
    var converter = new BpmnConverter();
    var workflow = await converter.ConvertFromXmlAsync(stream);

    var boundary = workflow.Activities.OfType<BoundaryTimerEvent>().Single();
    Assert.IsFalse(boundary.IsInterrupting);
}

[TestMethod]
public async Task ParseBoundaryEvent_NoAttribute_DefaultsToInterrupting()
{
    // Same BPMN but without cancelActivity attribute
    // Assert boundary.IsInterrupting == true
}

[TestMethod]
public async Task ParseBoundaryEvent_NonInterruptingMessage_SetsIsInterruptingFalse()
{
    // BPMN with <boundaryEvent cancelActivity="false"> + messageEventDefinition
    // Assert MessageBoundaryEvent.IsInterrupting == false
}
```

**Step 2: Run tests to verify they fail**

Expected: Tests fail because converter doesn't read `cancelActivity` attribute

**Step 3: Write implementation**

In `BpmnConverter.cs`, in the boundary event parsing loop (around line 366), read the `cancelActivity` attribute:

```csharp
foreach (var boundaryEl in scopeElement.Elements(Bpmn + "boundaryEvent"))
{
    var id = GetId(boundaryEl);
    var attachedToRef = boundaryEl.Attribute("attachedToRef")?.Value
        ?? throw new InvalidOperationException($"boundaryEvent '{id}' must have an attachedToRef attribute");

    // BPMN spec: cancelActivity defaults to true when absent
    var cancelActivityAttr = boundaryEl.Attribute("cancelActivity")?.Value;
    var isInterrupting = cancelActivityAttr == null || !bool.TryParse(cancelActivityAttr, out var val) || val;

    var timerDef = boundaryEl.Element(Bpmn + "timerEventDefinition");
    var errorDef = boundaryEl.Element(Bpmn + "errorEventDefinition");
    var messageDef = boundaryEl.Element(Bpmn + "messageEventDefinition");
    var signalDef = boundaryEl.Element(Bpmn + "signalEventDefinition");

    Activity activity;
    if (timerDef != null)
    {
        var timerDefinition = ParseTimerDefinition(timerDef);
        activity = new BoundaryTimerEvent(id, attachedToRef, timerDefinition, isInterrupting);
    }
    else if (messageDef != null)
    {
        var messageRef = messageDef.Attribute("messageRef")?.Value
            ?? throw new InvalidOperationException(
                $"boundaryEvent '{id}' messageEventDefinition must have a messageRef attribute");
        activity = new MessageBoundaryEvent(id, attachedToRef, messageRef, isInterrupting);
    }
    else if (signalDef != null)
    {
        var signalRef = signalDef.Attribute("signalRef")?.Value
            ?? throw new InvalidOperationException(
                $"boundaryEvent '{id}' signalEventDefinition must have a signalRef attribute");
        activity = new SignalBoundaryEvent(id, attachedToRef, signalRef, isInterrupting);
    }
    else
    {
        string? errorCode = errorDef?.Attribute("errorRef")?.Value;
        // Error boundaries are always interrupting per BPMN spec
        activity = new BoundaryErrorEvent(id, attachedToRef, errorCode, IsInterrupting: true);
    }

    activities.Add(activity);
    activityMap[id] = activity;
}
```

**Step 4: Run tests**

Run: `dotnet build && dotnet test --filter "ParseBoundaryEvent"` from `src/Fleans/`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs src/Fleans/Fleans.Infrastructure.Tests/
git commit -m "feat: parse cancelActivity BPMN attribute for non-interrupting boundaries"
```

---

### Task 4: Non-interrupting handler logic in BoundaryEventHandler

This is the core behavior change. When `IsInterrupting == false`, skip cancellation and keep other boundaries registered.

**Files:**
- Modify: `src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs`
- Test: `src/Fleans/Fleans.Application.Tests/BoundaryEventHandlerTests.cs`

**Step 1: Write failing tests**

Add to `BoundaryEventHandlerTests.cs`:

```csharp
[TestMethod]
public async Task HandleBoundaryTimerFired_NonInterrupting_DoesNotCancelAttachedActivity()
{
    // Arrange: set up workflow with non-interrupting timer boundary
    // Act: call HandleBoundaryTimerFiredAsync
    // Assert: attached activity NOT cancelled, boundary path created, other boundaries still registered
}

[TestMethod]
public async Task HandleBoundaryMessageFired_NonInterrupting_DoesNotCancelAttachedActivity()
{
    // Same pattern for message boundary
}

[TestMethod]
public async Task HandleBoundarySignalFired_NonInterrupting_DoesNotCancelAttachedActivity()
{
    // Same pattern for signal boundary
}
```

Note: Check the existing `BoundaryEventHandlerTests.cs` for the mock/arrange pattern used there, and follow it exactly.

**Step 2: Run tests to verify they fail**

**Step 3: Write implementation**

Refactor each handler method in `BoundaryEventHandler.cs`. Extract the interrupting logic into an `if` block. Example for timer:

```csharp
public async Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId, IWorkflowDefinition definition)
{
    var attachedActivityId = boundaryTimer.AttachedToActivityId;

    var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
        e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
    if (attachedEntry == null)
    {
        LogStaleBoundaryTimerIgnored(boundaryTimer.ActivityId, hostActivityInstanceId);
        return;
    }

    var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);

    if (boundaryTimer.IsInterrupting)
    {
        // Existing interrupting behavior — cancel attached activity
        await attachedInstance.Cancel($"Interrupted by boundary timer event '{boundaryTimer.ActivityId}'");
        await _accessor.CancelScopeChildren(attachedEntry.ActivityInstanceId);
        _accessor.State.CompleteEntries([attachedEntry]);

        var variablesId = await attachedInstance.GetVariablesStateId();
        await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, variablesId, definition);
        await UnsubscribeBoundarySignalSubscriptionsAsync(attachedActivityId, definition);
        LogBoundaryTimerInterrupted(boundaryTimer.ActivityId, attachedActivityId);
    }
    else
    {
        // Non-interrupting — attached activity continues, clone variable scope
        LogNonInterruptingBoundaryTimerFired(boundaryTimer.ActivityId, attachedActivityId);
    }

    await CreateAndExecuteBoundaryInstanceAsync(boundaryTimer, attachedInstance, definition,
        cloneVariables: !boundaryTimer.IsInterrupting);
}
```

Apply the same pattern to `HandleBoundaryMessageFiredAsync` and `HandleBoundarySignalFiredAsync`.

**Step 4: Update `CreateAndExecuteBoundaryInstanceAsync`**

Add `cloneVariables` parameter. When `true`, clone the variable scope instead of sharing it:

```csharp
private async Task CreateAndExecuteBoundaryInstanceAsync(
    Activity boundaryActivity, IActivityInstanceGrain sourceInstance,
    IWorkflowDefinition definition, bool cloneVariables = false)
{
    var boundaryInstanceId = Guid.NewGuid();
    var boundaryInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(boundaryInstanceId);
    var sourceVariablesId = await sourceInstance.GetVariablesStateId();

    Guid variablesId;
    if (cloneVariables)
    {
        variablesId = _accessor.State.AddCloneOfVariableState(sourceVariablesId);
    }
    else
    {
        variablesId = sourceVariablesId;
    }

    await boundaryInstance.SetActivity(boundaryActivity.ActivityId, boundaryActivity.GetType().Name);
    await boundaryInstance.SetVariablesId(variablesId);

    var boundaryEntry = new ActivityInstanceEntry(boundaryInstanceId, boundaryActivity.ActivityId, _accessor.State.Id);
    _accessor.State.AddEntries([boundaryEntry]);

    var commands = await boundaryActivity.ExecuteAsync(_accessor.WorkflowExecutionContext, boundaryInstance, definition);
    await _accessor.ProcessCommands(commands, boundaryEntry, boundaryInstance);
    await _accessor.TransitionToNextActivity();
    await _accessor.ExecuteWorkflow();
}
```

**Step 5: Add log messages**

Add to the logging section of `BoundaryEventHandler.cs`:

```csharp
[LoggerMessage(EventId = 4010, Level = LogLevel.Information,
    Message = "Non-interrupting boundary timer {BoundaryTimerId} fired — attached activity {AttachedActivityId} continues")]
private partial void LogNonInterruptingBoundaryTimerFired(string boundaryTimerId, string attachedActivityId);

[LoggerMessage(EventId = 4011, Level = LogLevel.Information,
    Message = "Non-interrupting boundary message {BoundaryMessageId} fired — attached activity {AttachedActivityId} continues")]
private partial void LogNonInterruptingBoundaryMessageFired(string boundaryMessageId, string attachedActivityId);

[LoggerMessage(EventId = 4012, Level = LogLevel.Information,
    Message = "Non-interrupting boundary signal {BoundarySignalId} fired — attached activity {AttachedActivityId} continues")]
private partial void LogNonInterruptingBoundarySignalFired(string boundarySignalId, string attachedActivityId);
```

**Step 6: Run tests**

Run: `dotnet build && dotnet test` from `src/Fleans/`
Expected: All tests pass (new + existing)

**Step 7: Commit**

```bash
git add src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs src/Fleans/Fleans.Application.Tests/
git commit -m "feat: non-interrupting boundary event handler logic"
```

---

### Task 5: Timer cycle re-registration for non-interrupting boundaries

**Files:**
- Modify: `src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/TimerDefinition.cs`
- Test: `src/Fleans/Fleans.Application.Tests/BoundaryTimerEventTests.cs`

**Step 1: Add `DecrementCycle` method to TimerDefinition**

Write failing test in a new or existing domain test file:

```csharp
[TestMethod]
public void TimerDefinition_DecrementCycle_DecrementsRepeatCount()
{
    var timer = new TimerDefinition(TimerType.Cycle, "R3/PT10S");
    var next = timer.DecrementCycle();
    Assert.IsNotNull(next);
    Assert.AreEqual("R2/PT10S", next!.Expression);
}

[TestMethod]
public void TimerDefinition_DecrementCycle_ReturnsNull_WhenCountReachesZero()
{
    var timer = new TimerDefinition(TimerType.Cycle, "R1/PT10S");
    var next = timer.DecrementCycle();
    Assert.IsNull(next);
}

[TestMethod]
public void TimerDefinition_DecrementCycle_InfiniteRepeat_ReturnsNewCycle()
{
    var timer = new TimerDefinition(TimerType.Cycle, "R/PT10S");
    var next = timer.DecrementCycle();
    Assert.IsNotNull(next);
    Assert.AreEqual("R/PT10S", next!.Expression);
}
```

**Step 2: Implement `DecrementCycle`**

Add to `TimerDefinition.cs`:

```csharp
public TimerDefinition? DecrementCycle()
{
    if (Type != TimerType.Cycle)
        throw new InvalidOperationException("DecrementCycle can only be called on Cycle timers");

    var (repeatCount, _) = ParseCycle();

    // Infinite repetition (R/PT...)
    if (repeatCount == null)
        return this;

    // Last repetition
    if (repeatCount <= 1)
        return null;

    var parts = Expression.Split('/');
    return new TimerDefinition(TimerType.Cycle, $"R{repeatCount - 1}/{parts[1]}");
}
```

**Step 3: Add cycle re-registration to handler**

In `BoundaryEventHandler.HandleBoundaryTimerFiredAsync`, after the non-interrupting branch creates the boundary instance, add cycle re-registration:

```csharp
// After CreateAndExecuteBoundaryInstanceAsync for non-interrupting timers:
if (!boundaryTimer.IsInterrupting && boundaryTimer.TimerDefinition.Type == TimerType.Cycle)
{
    var nextCycle = boundaryTimer.TimerDefinition.DecrementCycle();
    if (nextCycle != null)
    {
        var callbackGrain = _accessor.GrainFactory.GetGrain<ITimerCallbackGrain>(
            _accessor.State.Id, $"{hostActivityInstanceId}:{boundaryTimer.ActivityId}");
        await callbackGrain.Activate(nextCycle.GetDueTime());
        LogCycleTimerReRegistered(boundaryTimer.ActivityId, nextCycle.Expression);
    }
}
```

Add log message:
```csharp
[LoggerMessage(EventId = 4013, Level = LogLevel.Information,
    Message = "Cycle timer {TimerActivityId} re-registered with expression {CycleExpression}")]
private partial void LogCycleTimerReRegistered(string timerActivityId, string cycleExpression);
```

**Step 4: Run tests**

Run: `dotnet build && dotnet test` from `src/Fleans/`
Expected: All pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/TimerDefinition.cs src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs src/Fleans/Fleans.Domain.Tests/ src/Fleans/Fleans.Application.Tests/
git commit -m "feat: timer cycle re-registration for non-interrupting boundary timers"
```

---

### Task 6: Integration tests — non-interrupting timer boundary (Orleans cluster)

**Files:**
- Modify: `src/Fleans/Fleans.Application.Tests/BoundaryTimerEventTests.cs`

**Step 1: Write integration test**

Follow the existing pattern in `BoundaryTimerEventTests.cs` (extends `WorkflowTestBase`):

```csharp
[TestMethod]
public async Task NonInterruptingBoundaryTimer_AttachedActivityContinues()
{
    // Arrange: workflow with task1 + non-interrupting timer boundary bt1
    var start = new StartEvent("start");
    var task = new TaskActivity("task1");
    var timerDef = new TimerDefinition(TimerType.Duration, "PT1S");
    var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef, IsInterrupting: false);
    var afterTimer = new ScriptTask("afterTimer", "csharp", "_context.timerPath = true");
    var end1 = new EndEvent("end1");
    var end2 = new EndEvent("end2");

    var workflow = ActivityTestHelper.CreateWorkflowDefinition(
        [start, task, boundaryTimer, afterTimer, end1, end2],
        [
            new SequenceFlow("f1", start, task),
            new SequenceFlow("f2", task, end1),
            new SequenceFlow("f3", boundaryTimer, afterTimer),
            new SequenceFlow("f4", afterTimer, end2)
        ]);

    // Act
    var instanceId = Guid.NewGuid();
    var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
    await workflowInstance.SetWorkflow(workflow);
    await workflowInstance.StartWorkflow();

    // Get host activity instance ID for timer callback
    var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
    var hostInstanceId = preSnapshot.ActiveActivities
        .First(a => a.ActivityId == "task1").ActivityInstanceId;

    // Simulate timer firing
    await workflowInstance.HandleTimerFired("bt1", hostInstanceId);
    await Task.Delay(500); // Let async processing settle

    // Assert: task1 should still be active (not cancelled)
    var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
    Assert.IsFalse(midSnapshot.IsCompleted, "Workflow should not be completed yet");
    Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "task1"),
        "task1 should still be active");
    Assert.IsTrue(midSnapshot.CompletedActivities.Any(a => a.ActivityId == "afterTimer"),
        "Timer path should have executed");

    // Complete the attached activity normally
    await workflowInstance.CompleteActivity("task1", new ExpandoObject());
    await Task.Delay(500);

    // Assert: workflow should now be completed
    var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
    Assert.IsTrue(finalSnapshot.IsCompleted, "Workflow should be completed");
    Assert.IsFalse(finalSnapshot.CompletedActivities
        .First(a => a.ActivityId == "task1").IsCancelled,
        "task1 should NOT be cancelled");
}
```

**Step 2: Run test**

Run: `dotnet test --filter "NonInterruptingBoundaryTimer_AttachedActivityContinues"` from `src/Fleans/`
Expected: PASS (if handler logic from Task 4 is correct)

**Step 3: Write regression test for interrupting behavior**

```csharp
[TestMethod]
public async Task InterruptingBoundaryTimer_StillCancelsAttachedActivity()
{
    // Same workflow but with IsInterrupting: true (default)
    // Simulate timer firing, assert task1 IS cancelled
}
```

**Step 4: Run full test suite**

Run: `dotnet test` from `src/Fleans/`
Expected: All pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/BoundaryTimerEventTests.cs
git commit -m "test: integration tests for non-interrupting timer boundary"
```

---

### Task 7: Integration tests — non-interrupting message and signal boundaries

**Files:**
- Modify: `src/Fleans/Fleans.Application.Tests/MessageBoundaryEventTests.cs`
- Modify: `src/Fleans/Fleans.Application.Tests/SignalBoundaryEventTests.cs`

**Step 1: Write message boundary integration test**

Follow existing pattern in `MessageBoundaryEventTests.cs`:

```csharp
[TestMethod]
public async Task NonInterruptingMessageBoundary_AttachedActivityContinues()
{
    // Arrange: workflow with task1 + non-interrupting message boundary
    // Act: deliver message to trigger boundary
    // Assert: task1 still active, boundary path executed
    // Complete task1 normally
    // Assert: workflow completed, task1 not cancelled
}
```

**Step 2: Write signal boundary integration test**

Follow existing pattern in `SignalBoundaryEventTests.cs`:

```csharp
[TestMethod]
public async Task NonInterruptingSignalBoundary_AttachedActivityContinues()
{
    // Same pattern with signal delivery instead of message
}
```

**Step 3: Run tests**

Run: `dotnet test` from `src/Fleans/`
Expected: All pass

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/MessageBoundaryEventTests.cs src/Fleans/Fleans.Application.Tests/SignalBoundaryEventTests.cs
git commit -m "test: integration tests for non-interrupting message and signal boundaries"
```

---

### Task 8: Manual test fixtures

**Files:**
- Create: `tests/manual/15-non-interrupting-boundaries/non-interrupting-timer.bpmn`
- Create: `tests/manual/15-non-interrupting-boundaries/non-interrupting-message.bpmn`
- Create: `tests/manual/15-non-interrupting-boundaries/timer-cycle.bpmn`
- Create: `tests/manual/15-non-interrupting-boundaries/test-plan.md`

**Step 1: Create BPMN fixtures**

`non-interrupting-timer.bpmn`: Task with `cancelActivity="false"` timer boundary (PT10S). Timer path sets `reminder=true`. After boundary fires, complete the task via API → both paths complete.

`non-interrupting-message.bpmn`: Timer catch (PT60S) with `cancelActivity="false"` message boundary. Send message → boundary path executes, timer still waiting. Timer fires → workflow completes both paths.

`timer-cycle.bpmn`: Task with `cancelActivity="false"` timer cycle boundary (`R3/PT5S`). Each cycle fire increments a counter. Complete task after 20s → verify 3 boundary fires.

All fixtures must include `<bpmndi:BPMNDiagram>` section and use `scriptFormat="csharp"`.

**Step 2: Create test-plan.md**

Document prerequisites, steps (deploy, start, trigger, verify), and expected outcomes for each scenario.

**Step 3: Commit**

```bash
git add tests/manual/15-non-interrupting-boundaries/
git commit -m "test: manual test fixtures for non-interrupting boundary events"
```

---

### Task 9: Update architecture audit

**Files:**
- Modify: `docs/plans/2026-02-17-architectural-risk-audit.md`

**Step 1: Mark 4.1 as done**

Change the checklist item:
```markdown
- [x] **4.1 — Non-interrupting boundary events**: Added `IsInterrupting` flag to boundary events. Non-interrupting boundaries skip cancellation, clone variable scope, and spawn parallel branch. Timer cycle support for repeating non-interrupting timers. *Done.*
```

**Step 2: Commit**

```bash
git add docs/plans/2026-02-17-architectural-risk-audit.md
git commit -m "docs: mark non-interrupting boundaries (4.1) as done in arch audit"
```
