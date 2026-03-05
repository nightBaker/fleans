# Test Consolidation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reduce ~565 LOC of duplicated test code by extracting shared patterns into abstract base classes, without removing any test scenarios.

**Architecture:** Create abstract base test classes for boundary events (domain + app layers) and intermediate catch events (domain layer). Each base class encodes the shared test pattern; thin subclasses provide event-type-specific creation and configuration. Gateway tests get shared helpers extracted into `ActivityTestHelper`.

**Tech Stack:** MSTest, NSubstitute, Orleans.TestingHost, C# abstract classes with inherited `[TestMethod]` attributes.

---

### Task 0: Establish green baseline

**Files:**
- None (read-only)

**Step 1: Run all tests to confirm baseline is green**

Run: `cd src/Fleans && dotnet test --verbosity quiet`
Expected: All 465 tests pass (0 failures)

**Step 2: Record test count per project**

Run: `cd src/Fleans && dotnet test --verbosity quiet --logger "console;verbosity=minimal" 2>&1 | grep -E "Passed|Failed|Total"`
Expected: Note exact counts for verification after each refactoring task.

---

### Task 1: Domain Boundary Event Tests — Extract Base Class

**Files:**
- Create: `src/Fleans/Fleans.Domain.Tests/BoundaryEventDomainTestBase.cs`
- Modify: `src/Fleans/Fleans.Domain.Tests/BoundaryTimerEventDomainTests.cs`
- Modify: `src/Fleans/Fleans.Domain.Tests/MessageBoundaryEventDomainTests.cs`
- Modify: `src/Fleans/Fleans.Domain.Tests/SignalBoundaryEventDomainTests.cs`

**Step 1: Create the base class**

Create `src/Fleans/Fleans.Domain.Tests/BoundaryEventDomainTestBase.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

public abstract class BoundaryEventDomainTestBase
{
    protected abstract Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true);

    protected abstract void AssertEventSpecificProperties(Activity boundary);

    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately()
    {
        // Arrange
        var boundary = CreateBoundaryEvent("b1", "task1");
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundary, recovery],
            [new SequenceFlow("seq1", boundary, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("b1");

        // Act
        var commands = await boundary.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("b1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var boundary = CreateBoundaryEvent("b1", "task1");
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundary, recovery],
            [new SequenceFlow("seq1", boundary, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("b1");

        // Act
        var nextActivities = await boundary.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.AreEqual(1, nextActivities.Count);
        Assert.AreEqual("recovery", nextActivities[0].NextActivity.ActivityId);
    }

    [TestMethod]
    public void BoundaryEvent_ShouldHaveCorrectProperties()
    {
        var boundary = CreateBoundaryEvent("b1", "task1");
        Assert.AreEqual("b1", boundary.ActivityId);
        AssertEventSpecificProperties(boundary);
    }

    [TestMethod]
    public void BoundaryEvent_IsInterrupting_DefaultsToTrue()
    {
        var boundary = (IBoundaryEvent)CreateBoundaryEvent("b1", "task1");
        Assert.IsTrue(boundary.IsInterrupting);
    }

    [TestMethod]
    public void BoundaryEvent_IsInterrupting_CanBeSetToFalse()
    {
        var boundary = (IBoundaryEvent)CreateBoundaryEvent("b1", "task1", isInterrupting: false);
        Assert.IsFalse(boundary.IsInterrupting);
    }
}
```

**Step 2: Refactor BoundaryTimerEventDomainTests to use base class**

Replace `src/Fleans/Fleans.Domain.Tests/BoundaryTimerEventDomainTests.cs` with:

```csharp
using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class BoundaryTimerEventDomainTests : BoundaryEventDomainTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new BoundaryTimerEvent(boundaryId, attachedToId,
            new TimerDefinition(TimerType.Duration, "PT30M"), IsInterrupting: isInterrupting);

    protected override void AssertEventSpecificProperties(Activity boundary)
    {
        var timer = (BoundaryTimerEvent)boundary;
        Assert.AreEqual("task1", timer.AttachedToActivityId);
        Assert.AreEqual(TimerType.Duration, timer.TimerDefinition.Type);
        Assert.AreEqual("PT30M", timer.TimerDefinition.Expression);
    }
}
```

**Step 3: Run tests to verify refactoring preserved behavior**

Run: `cd src/Fleans && dotnet test --filter "FullyQualifiedName~BoundaryTimerEventDomainTests" --verbosity quiet`
Expected: 5 tests pass

**Step 4: Refactor MessageBoundaryEventDomainTests**

Replace `src/Fleans/Fleans.Domain.Tests/MessageBoundaryEventDomainTests.cs` with:

```csharp
using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class MessageBoundaryEventDomainTests : BoundaryEventDomainTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new MessageBoundaryEvent(boundaryId, attachedToId, "msg_payment", IsInterrupting: isInterrupting);

    protected override void AssertEventSpecificProperties(Activity boundary)
    {
        var msg = (MessageBoundaryEvent)boundary;
        Assert.AreEqual("task1", msg.AttachedToActivityId);
        Assert.AreEqual("msg_payment", msg.MessageDefinitionId);
    }
}
```

**Step 5: Run tests**

Run: `cd src/Fleans && dotnet test --filter "FullyQualifiedName~MessageBoundaryEventDomainTests" --verbosity quiet`
Expected: 5 tests pass

**Step 6: Refactor SignalBoundaryEventDomainTests**

Replace `src/Fleans/Fleans.Domain.Tests/SignalBoundaryEventDomainTests.cs` with:

```csharp
using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class SignalBoundaryEventDomainTests : BoundaryEventDomainTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new SignalBoundaryEvent(boundaryId, attachedToId, "sig_order", IsInterrupting: isInterrupting);

    protected override void AssertEventSpecificProperties(Activity boundary)
    {
        var sig = (SignalBoundaryEvent)boundary;
        Assert.AreEqual("task1", sig.AttachedToActivityId);
        Assert.AreEqual("sig_order", sig.SignalDefinitionId);
    }
}
```

**Step 7: Run all domain tests**

Run: `cd src/Fleans && dotnet test --filter "FullyQualifiedName~Domain.Tests" --verbosity quiet`
Expected: All domain tests pass, same count as baseline

**Step 8: Commit**

```bash
git add src/Fleans/Fleans.Domain.Tests/BoundaryEventDomainTestBase.cs \
        src/Fleans/Fleans.Domain.Tests/BoundaryTimerEventDomainTests.cs \
        src/Fleans/Fleans.Domain.Tests/MessageBoundaryEventDomainTests.cs \
        src/Fleans/Fleans.Domain.Tests/SignalBoundaryEventDomainTests.cs
git commit -m "refactor: extract BoundaryEventDomainTestBase from 3 boundary event domain test files"
```

---

### Task 2: Domain Catch Event Tests — Extract Base Class

**Files:**
- Create: `src/Fleans/Fleans.Domain.Tests/CatchEventDomainTestBase.cs`
- Modify: `src/Fleans/Fleans.Domain.Tests/TimerIntermediateCatchEventDomainTests.cs`
- Modify: `src/Fleans/Fleans.Domain.Tests/MessageIntermediateCatchEventDomainTests.cs`
- Modify: `src/Fleans/Fleans.Domain.Tests/SignalIntermediateCatchEventDomainTests.cs`

**Context:** The three catch event domain tests share `GetNextActivities` logic identically. The `ExecuteAsync` tests differ: Timer has no command to assert, Message asserts `RegisterMessageCommand`, Signal asserts `RegisterSignalCommand`. The base class handles the shared `GetNextActivities` test and the common `ExecuteAsync` pattern (Execute called, Complete NOT called, event published). Command-specific assertions stay in subclasses.

**Step 1: Create the base class**

Create `src/Fleans/Fleans.Domain.Tests/CatchEventDomainTestBase.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

public abstract class CatchEventDomainTestBase
{
    protected abstract string CatchEventId { get; }
    protected abstract string ExpectedTypeName { get; }

    protected abstract Activity CreateCatchEvent(string activityId);

    protected abstract WorkflowDefinition CreateDefinition(
        List<Activity> activities, List<SequenceFlow> sequenceFlows);

    protected virtual void AssertExecuteCommands(IReadOnlyList<IExecutionCommand> commands) { }

    [TestMethod]
    public async Task ExecuteAsync_ShouldCallExecute_AndNotComplete()
    {
        // Arrange
        var catchEvent = CreateCatchEvent(CatchEventId);
        var end = new EndEvent("end");
        var definition = CreateDefinition(
            [catchEvent, end],
            [new SequenceFlow("seq1", catchEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext(CatchEventId);

        // Act
        var commands = await catchEvent.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — should execute but NOT complete (waits for external event)
        await activityContext.Received(1).Execute();
        await activityContext.DidNotReceive().Complete();
        AssertExecuteCommands(commands);
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual(CatchEventId, executedEvent.activityId);
        Assert.AreEqual(ExpectedTypeName, executedEvent.TypeName);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var catchEvent = CreateCatchEvent(CatchEventId);
        var end = new EndEvent("end");
        var definition = CreateDefinition(
            [catchEvent, end],
            [new SequenceFlow("seq1", catchEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext(CatchEventId);

        // Act
        var nextActivities = await catchEvent.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.AreEqual(1, nextActivities.Count);
        Assert.AreEqual("end", nextActivities[0].NextActivity.ActivityId);
    }
}
```

**Step 2: Refactor TimerIntermediateCatchEventDomainTests**

Replace `src/Fleans/Fleans.Domain.Tests/TimerIntermediateCatchEventDomainTests.cs` with:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Tests;

[TestClass]
public class TimerIntermediateCatchEventDomainTests : CatchEventDomainTestBase
{
    protected override string CatchEventId => "timer1";
    protected override string ExpectedTypeName => "TimerIntermediateCatchEvent";

    protected override Activity CreateCatchEvent(string activityId)
        => new TimerIntermediateCatchEvent(activityId, new TimerDefinition(TimerType.Duration, "PT5M"));

    protected override WorkflowDefinition CreateDefinition(
        List<Activity> activities, List<SequenceFlow> sequenceFlows)
        => ActivityTestHelper.CreateWorkflowDefinition(activities, sequenceFlows);
}
```

**Step 3: Run tests**

Run: `cd src/Fleans && dotnet test --filter "FullyQualifiedName~TimerIntermediateCatchEventDomainTests" --verbosity quiet`
Expected: 2 tests pass

**Step 4: Refactor MessageIntermediateCatchEventDomainTests**

Replace `src/Fleans/Fleans.Domain.Tests/MessageIntermediateCatchEventDomainTests.cs` with:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Tests;

[TestClass]
public class MessageIntermediateCatchEventDomainTests : CatchEventDomainTestBase
{
    protected override string CatchEventId => "msgCatch1";
    protected override string ExpectedTypeName => "MessageIntermediateCatchEvent";

    protected override Activity CreateCatchEvent(string activityId)
        => new MessageIntermediateCatchEvent(activityId, "msg_payment");

    protected override WorkflowDefinition CreateDefinition(
        List<Activity> activities, List<SequenceFlow> sequenceFlows)
        => ActivityTestHelper.CreateWorkflowDefinition(activities, sequenceFlows);

    protected override void AssertExecuteCommands(IReadOnlyList<IExecutionCommand> commands)
    {
        var msgCmd = commands.OfType<RegisterMessageCommand>().Single();
        Assert.AreEqual("msg_payment", msgCmd.MessageDefinitionId);
        Assert.AreEqual(CatchEventId, msgCmd.ActivityId);
        Assert.IsFalse(msgCmd.IsBoundary);
    }
}
```

**Step 5: Run tests**

Run: `cd src/Fleans && dotnet test --filter "FullyQualifiedName~MessageIntermediateCatchEventDomainTests" --verbosity quiet`
Expected: 2 tests pass

**Step 6: Refactor SignalIntermediateCatchEventDomainTests**

Replace `src/Fleans/Fleans.Domain.Tests/SignalIntermediateCatchEventDomainTests.cs` with:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Tests;

[TestClass]
public class SignalIntermediateCatchEventDomainTests : CatchEventDomainTestBase
{
    protected override string CatchEventId => "sigCatch1";
    protected override string ExpectedTypeName => "SignalIntermediateCatchEvent";

    protected override Activity CreateCatchEvent(string activityId)
        => new SignalIntermediateCatchEvent(activityId, "sig1");

    protected override WorkflowDefinition CreateDefinition(
        List<Activity> activities, List<SequenceFlow> sequenceFlows)
        => ActivityTestHelper.CreateDefinitionWithSignal(activities, sequenceFlows);

    protected override void AssertExecuteCommands(IReadOnlyList<IExecutionCommand> commands)
    {
        var sigCmd = commands.OfType<RegisterSignalCommand>().Single();
        Assert.AreEqual("order_shipped", sigCmd.SignalName);
        Assert.AreEqual(CatchEventId, sigCmd.ActivityId);
        Assert.IsFalse(sigCmd.IsBoundary);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoOutgoingFlow()
    {
        // Arrange
        var sigCatch = CreateCatchEvent(CatchEventId);
        var definition = CreateDefinition([sigCatch], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext(CatchEventId);

        // Act
        var nextActivities = await sigCatch.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.AreEqual(0, nextActivities.Count);
    }
}
```

**Step 7: Run all domain tests**

Run: `cd src/Fleans && dotnet test --filter "FullyQualifiedName~Domain.Tests" --verbosity quiet`
Expected: All domain tests pass, same count as baseline

**Step 8: Commit**

```bash
git add src/Fleans/Fleans.Domain.Tests/CatchEventDomainTestBase.cs \
        src/Fleans/Fleans.Domain.Tests/TimerIntermediateCatchEventDomainTests.cs \
        src/Fleans/Fleans.Domain.Tests/MessageIntermediateCatchEventDomainTests.cs \
        src/Fleans/Fleans.Domain.Tests/SignalIntermediateCatchEventDomainTests.cs
git commit -m "refactor: extract CatchEventDomainTestBase from 3 catch event domain test files"
```

---

### Task 3: Application Boundary Event Tests — Extract Base Class

**Files:**
- Create: `src/Fleans/Fleans.Application.Tests/BoundaryEventTestBase.cs`
- Modify: `src/Fleans/Fleans.Application.Tests/BoundaryTimerEventTests.cs`
- Modify: `src/Fleans/Fleans.Application.Tests/MessageBoundaryEventTests.cs`
- Modify: `src/Fleans/Fleans.Application.Tests/SignalBoundaryEventTests.cs`

**Context:** The 3 shared test patterns are: (1) event arrives first → boundary path, (2) task completes first → normal flow, (3) non-interrupting → attached activity continues. Each subclass provides: boundary event creation, workflow definition configuration (Messages/Signals collections), initial state setup (correlation variables), and the trigger mechanism. Event-specific tests (stale events, regression tests) remain in subclasses.

**Step 1: Create the base class**

Create `src/Fleans/Fleans.Application.Tests/BoundaryEventTestBase.cs`:

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

public abstract class BoundaryEventTestBase : WorkflowTestBase
{
    /// <summary>Create boundary event attached to the given activity.</summary>
    protected abstract Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true);

    /// <summary>Set Messages/Signals/etc. on the workflow definition.</summary>
    protected abstract void ConfigureWorkflowDefinition(WorkflowDefinition definition);

    /// <summary>Set initial variables (e.g., correlation keys). Default: no-op.</summary>
    protected virtual Task SetupInitialState(IWorkflowInstanceGrain instance) => Task.CompletedTask;

    /// <summary>Trigger the boundary event (timer fired, message delivered, signal broadcast).</summary>
    protected abstract Task TriggerBoundaryEvent(IWorkflowInstanceGrain instance, Guid hostInstanceId);

    [TestMethod]
    public async Task BoundaryEvent_EventArrivesFirst_ShouldFollowBoundaryFlow()
    {
        // Arrange — Start → Task(+Boundary) → End, Boundary → Recovery → BoundaryEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = CreateBoundaryEvent("boundary1", "task1");
        var end = new EndEvent("end");
        var recovery = new TaskActivity("recovery");
        var boundaryEnd = new EndEvent("boundaryEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-event-arrives-first",
            Activities = [start, task, boundary, end, recovery, boundaryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, recovery),
                new SequenceFlow("f4", recovery, boundaryEnd)
            ]
        };
        ConfigureWorkflowDefinition(workflow);

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await SetupInitialState(instance);
        await instance.StartWorkflow();

        // Verify task is active
        var instanceId = instance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "Task should be active");
        var hostInstanceId = preSnapshot.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        // Act — trigger boundary event
        await TriggerBoundaryEvent(instance, hostInstanceId);

        // Assert — boundary path taken, task interrupted
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted, "Workflow should NOT be completed yet — recovery pending");
        var interruptedTask = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(interruptedTask, "Original task should be completed (interrupted)");
        Assert.IsTrue(interruptedTask.IsCancelled, "Interrupted task should be cancelled");
        Assert.IsNotNull(interruptedTask.CancellationReason, "Cancelled task should have a reason");
        Assert.IsNull(interruptedTask.ErrorState, "Cancelled task should not have error state");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "recovery"),
            "Recovery task should be active");

        // Complete recovery
        await instance.CompleteActivity("recovery", new ExpandoObject());
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed after recovery");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "boundaryEnd"),
            "Should complete via boundary end");
    }

    [TestMethod]
    public async Task BoundaryEvent_TaskCompletesFirst_ShouldFollowNormalFlow()
    {
        // Arrange — Start → Task(+Boundary) → End, Boundary → BoundaryEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = CreateBoundaryEvent("boundary1", "task1");
        var end = new EndEvent("end");
        var boundaryEnd = new EndEvent("boundaryEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-task-completes-first",
            Activities = [start, task, boundary, end, boundaryEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, boundaryEnd)
            ]
        };
        ConfigureWorkflowDefinition(workflow);

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await SetupInitialState(instance);
        await instance.StartWorkflow();

        // Act — complete task normally
        await instance.CompleteActivity("task1", new ExpandoObject());

        // Assert — normal flow
        var instanceId = instance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed via normal flow");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should complete via normal end event");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "boundaryEnd"),
            "Should NOT complete via boundary end event");
    }

    [TestMethod]
    public async Task NonInterruptingBoundary_AttachedActivityContinues()
    {
        // Arrange — Start → Task(+NonInterruptingBoundary) → End1, Boundary → AfterBoundary → End2
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var boundary = CreateBoundaryEvent("boundary1", "task1", isInterrupting: false);
        var afterBoundary = new TaskActivity("afterBoundary");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ni-boundary-test",
            Activities = [start, task, boundary, afterBoundary, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end1),
                new SequenceFlow("f3", boundary, afterBoundary),
                new SequenceFlow("f4", afterBoundary, end2)
            ]
        };
        ConfigureWorkflowDefinition(workflow);

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        await SetupInitialState(instance);
        await instance.StartWorkflow();

        // Get host activity instance ID
        var instanceId = instance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        // Act — trigger non-interrupting boundary event
        await TriggerBoundaryEvent(instance, hostInstanceId);

        // Assert — task1 still active, afterBoundary also active
        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(midSnapshot!.IsCompleted, "Workflow should not be completed yet");
        Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "task1 should still be active after non-interrupting event");
        Assert.IsTrue(midSnapshot.ActiveActivities.Any(a => a.ActivityId == "afterBoundary"),
            "afterBoundary should be active on boundary path");
        Assert.IsFalse(midSnapshot.CompletedActivities.Any(a => a.ActivityId == "task1" && a.IsCancelled),
            "task1 should NOT be cancelled");

        // Complete the attached activity normally
        await instance.CompleteActivity("task1", new ExpandoObject());

        // Assert: workflow completed, task1 not cancelled
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");
        var task1Entry = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "task1");
        Assert.IsFalse(task1Entry.IsCancelled, "task1 should NOT be cancelled");
    }
}
```

**Step 2: Refactor BoundaryTimerEventTests**

Replace `src/Fleans/Fleans.Application.Tests/BoundaryTimerEventTests.cs` with:

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class BoundaryTimerEventTests : BoundaryEventTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new BoundaryTimerEvent(boundaryId, attachedToId,
            new TimerDefinition(TimerType.Duration, "PT30M"), IsInterrupting: isInterrupting);

    protected override void ConfigureWorkflowDefinition(WorkflowDefinition definition) { }

    protected override async Task TriggerBoundaryEvent(IWorkflowInstanceGrain instance, Guid hostInstanceId)
        => await instance.HandleTimerFired("boundary1", hostInstanceId);

    [TestMethod]
    public async Task InterruptingBoundaryTimer_StillCancelsAttachedActivity()
    {
        // Regression test: verify interrupting behavior cancels task1
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var afterTimer = new TaskActivity("afterTimer");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "i-timer-regression",
            Activities = [start, task, boundaryTimer, afterTimer, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end1),
                new SequenceFlow("f3", boundaryTimer, afterTimer),
                new SequenceFlow("f4", afterTimer, end2)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        await workflowInstance.HandleTimerFired("bt1", hostInstanceId);

        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        var task1Entry = snapshot!.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(task1Entry, "task1 should be completed (interrupted)");
        Assert.IsTrue(task1Entry.IsCancelled, "task1 should be cancelled by interrupting timer");
    }
}
```

**Step 3: Run tests**

Run: `cd src/Fleans && dotnet test --filter "FullyQualifiedName~BoundaryTimerEventTests" --verbosity quiet`
Expected: 4 tests pass (3 inherited + 1 local)

**Step 4: Refactor MessageBoundaryEventTests**

Replace `src/Fleans/Fleans.Application.Tests/MessageBoundaryEventTests.cs` with:

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class MessageBoundaryEventTests : BoundaryEventTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new MessageBoundaryEvent(boundaryId, attachedToId, "msg1", IsInterrupting: isInterrupting);

    protected override void ConfigureWorkflowDefinition(WorkflowDefinition definition)
    {
        definition.Messages = [new MessageDefinition("msg1", "cancelOrder", "orderId")];
    }

    protected override async Task SetupInitialState(IWorkflowInstanceGrain instance)
    {
        dynamic initVars = new ExpandoObject();
        initVars.orderId = "order-456";
        await instance.SetInitialVariables(initVars);
    }

    protected override async Task TriggerBoundaryEvent(IWorkflowInstanceGrain instance, Guid hostInstanceId)
    {
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("cancelOrder");
        var delivered = await correlationGrain.DeliverMessage("order-456", new ExpandoObject());
        Assert.IsTrue(delivered, "Message should be delivered");
    }

    [TestMethod]
    public async Task BoundaryMessage_DirectCallAfterCompletion_ShouldBeSilentlyIgnored()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var msgDef = new MessageDefinition("msg1", "cancelOrder", "orderId");
        var boundaryMsg = new MessageBoundaryEvent("bmsg1", "task1", "msg1");
        var end = new EndEvent("end");
        var msgEnd = new EndEvent("msgEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-msg-stale",
            Activities = [start, task, boundaryMsg, end, msgEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundaryMsg, msgEnd)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        dynamic initVars = new ExpandoObject();
        initVars.orderId = "order-stale";
        await workflowInstance.SetInitialVariables(initVars);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnapshot!.IsCompleted);

        await workflowInstance.HandleBoundaryMessageFired("bmsg1", hostInstanceId);

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"));
    }
}
```

**Step 5: Run tests**

Run: `cd src/Fleans && dotnet test --filter "FullyQualifiedName~MessageBoundaryEventTests" --verbosity quiet`
Expected: 4 tests pass (3 inherited + 1 local)

**Step 6: Refactor SignalBoundaryEventTests**

Replace `src/Fleans/Fleans.Application.Tests/SignalBoundaryEventTests.cs` with:

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class SignalBoundaryEventTests : BoundaryEventTestBase
{
    protected override Activity CreateBoundaryEvent(string boundaryId, string attachedToId, bool isInterrupting = true)
        => new SignalBoundaryEvent(boundaryId, attachedToId, "sig1", IsInterrupting: isInterrupting);

    protected override void ConfigureWorkflowDefinition(WorkflowDefinition definition)
    {
        definition.Signals = [new SignalDefinition("sig1", "cancelOrder")];
    }

    protected override async Task TriggerBoundaryEvent(IWorkflowInstanceGrain instance, Guid hostInstanceId)
    {
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("cancelOrder");
        var deliveredCount = await signalGrain.BroadcastSignal();
        Assert.AreEqual(1, deliveredCount, "Signal should be delivered");
    }

    [TestMethod]
    public async Task BoundarySignal_StaleSignal_ShouldBeSilentlyIgnored()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalDef = new SignalDefinition("sig1", "cancelOrder");
        var boundarySignal = new SignalBoundaryEvent("bsig1", "task1", "sig1");
        var end = new EndEvent("end");
        var sigEnd = new EndEvent("sigEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-signal-stale",
            Activities = [start, task, boundarySignal, end, sigEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundarySignal, sigEnd)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities
            .First(a => a.ActivityId == "task1").ActivityInstanceId;

        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnapshot!.IsCompleted);

        await workflowInstance.HandleBoundarySignalFired("bsig1", hostInstanceId);

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"));
    }
}
```

**Step 7: Run all application tests**

Run: `cd src/Fleans && dotnet test --filter "FullyQualifiedName~Application.Tests" --verbosity quiet`
Expected: All application tests pass, same count as baseline

**Step 8: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/BoundaryEventTestBase.cs \
        src/Fleans/Fleans.Application.Tests/BoundaryTimerEventTests.cs \
        src/Fleans/Fleans.Application.Tests/MessageBoundaryEventTests.cs \
        src/Fleans/Fleans.Application.Tests/SignalBoundaryEventTests.cs
git commit -m "refactor: extract BoundaryEventTestBase from 3 boundary event integration test files"
```

---

### Task 4: Extract Gateway Condition Setup Helpers

**Files:**
- Modify: `src/Fleans/Fleans.Domain.Tests/ActivityTestHelper.cs`
- Modify: `src/Fleans/Fleans.Domain.Tests/ExclusiveGatewayActivityTests.cs`
- Modify: `src/Fleans/Fleans.Domain.Tests/InclusiveGatewayActivityTests.cs`

**Context:** Both ExclusiveGateway and InclusiveGateway tests repeat the same pattern of building `conditionStates` dictionaries and configuring `workflowContext.GetConditionSequenceStates()`. Extract a helper to reduce this boilerplate.

**Step 1: Add helper method to ActivityTestHelper**

Add to `src/Fleans/Fleans.Domain.Tests/ActivityTestHelper.cs`:

```csharp
public static void SetupConditionStates(
    IWorkflowExecutionContext workflowContext,
    Guid activityInstanceId,
    params (string sequenceFlowId, bool result)[] conditions)
{
    var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
    {
        [activityInstanceId] = conditions
            .Select(c => CreateEvaluatedConditionState(c.sequenceFlowId, activityInstanceId, c.result))
            .ToArray()
    };
    workflowContext.GetConditionSequenceStates()
        .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));
}
```

**Step 2: Refactor ExclusiveGatewayActivityTests to use helper**

In `src/Fleans/Fleans.Domain.Tests/ExclusiveGatewayActivityTests.cs`, replace each occurrence of the condition state setup pattern. For example, in `GetNextActivities_ShouldReturnTrueConditionTarget`, replace:

```csharp
        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] =
            [
                ActivityTestHelper.CreateEvaluatedConditionState("seq1", activityInstanceId, true),
                ActivityTestHelper.CreateEvaluatedConditionState("seq2", activityInstanceId, false)
            ]
        };
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));
```

With:

```csharp
        ActivityTestHelper.SetupConditionStates(workflowContext, activityInstanceId,
            ("seq1", true), ("seq2", false));
```

Apply the same replacement to all 3 tests in ExclusiveGatewayActivityTests that use this pattern:
- `GetNextActivities_ShouldReturnTrueConditionTarget`
- `GetNextActivities_ShouldReturnDefaultFlow_WhenNoTrueCondition`
- `GetNextActivities_ShouldThrow_WhenNoTrueConditionAndNoDefaultFlow`

**Step 3: Refactor InclusiveGatewayActivityTests to use helper**

Apply the same replacement in InclusiveGatewayActivityTests for these tests:
- `GetNextActivities_ShouldReturnAllTrueTargets`
- `GetNextActivities_ShouldReturnDefault_WhenAllConditionsFalse`
- `GetNextActivities_ShouldThrow_WhenAllConditionsFalseAndNoDefaultFlow`

Note: The `SetConditionResult_*` tests use unevaluated `ConditionSequenceState` objects (calling `SetResult` manually), so those should NOT use this helper — they have different setup requirements.

**Step 4: Run all domain tests**

Run: `cd src/Fleans && dotnet test --filter "FullyQualifiedName~Domain.Tests" --verbosity quiet`
Expected: All domain tests pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain.Tests/ActivityTestHelper.cs \
        src/Fleans/Fleans.Domain.Tests/ExclusiveGatewayActivityTests.cs \
        src/Fleans/Fleans.Domain.Tests/InclusiveGatewayActivityTests.cs
git commit -m "refactor: extract SetupConditionStates helper for gateway tests"
```

---

### Task 5: Final Verification

**Step 1: Run full test suite**

Run: `cd src/Fleans && dotnet test --verbosity quiet`
Expected: All 465 tests pass (identical count to Task 0 baseline)

**Step 2: Verify build is clean**

Run: `cd src/Fleans && dotnet build --no-restore 2>&1 | tail -5`
Expected: Build succeeded, 0 warnings (or same warnings as baseline)

**Step 3: Commit any remaining changes (if needed)**

Only if there are fixups needed from the verification step.
