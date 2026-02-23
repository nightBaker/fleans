# Event-Based Gateway Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement BPMN Event-Based Gateway (Phase 1.7) — a branching point where the first event to fire wins and all other pending event subscriptions are cancelled.

**Architecture:** `EventBasedGateway` activity completes immediately and transitions to all outgoing catch events. Each catch event registers its subscription (timer/message/signal). When any event fires, `CompleteActivityState` detects the Event-Based Gateway context and cancels all sibling catch events using existing unsubscribe infrastructure.

**Tech Stack:** Orleans grains, `[GenerateSerializer]`, MSTest + Orleans.TestingHost, NSubstitute

---

## Task 1: Domain — EventBasedGateway activity class + domain tests

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/EventBasedGateway.cs`
- Create: `src/Fleans/Fleans.Domain.Tests/EventBasedGatewayActivityTests.cs`

**Step 1: Write failing domain tests**

Create `src/Fleans/Fleans.Domain.Tests/EventBasedGatewayActivityTests.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class EventBasedGatewayActivityTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallComplete()
    {
        // Arrange
        var gateway = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timer1", new TimerDefinition(TimerType.Duration, "PT1H"));
        var msgCatch = new MessageIntermediateCatchEvent("msg1", "msgDef1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, timerCatch, msgCatch],
            [
                new SequenceFlow("f1", gateway, timerCatch),
                new SequenceFlow("f2", gateway, msgCatch)
            ]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("ebg1");

        // Act
        await gateway.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert
        await activityContext.Received(1).Complete();
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnAllOutgoingFlowTargets()
    {
        // Arrange
        var gateway = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timer1", new TimerDefinition(TimerType.Duration, "PT1H"));
        var msgCatch = new MessageIntermediateCatchEvent("msg1", "msgDef1");
        var sigCatch = new SignalIntermediateCatchEvent("sig1", "sigDef1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [gateway, timerCatch, msgCatch, sigCatch],
            [
                new SequenceFlow("f1", gateway, timerCatch),
                new SequenceFlow("f2", gateway, msgCatch),
                new SequenceFlow("f3", gateway, sigCatch)
            ]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("ebg1");

        // Act
        var nextActivities = await gateway.GetNextActivities(workflowContext, activityContext, definition);

        // Assert
        Assert.HasCount(3, nextActivities);
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "timer1"));
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "msg1"));
        Assert.IsTrue(nextActivities.Any(a => a.ActivityId == "sig1"));
    }
}
```

**Step 2: Run tests — verify they fail**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~EventBasedGateway" --verbosity minimal`
Expected: FAIL — `EventBasedGateway` class doesn't exist.

**Step 3: Write the activity class**

Create `src/Fleans/Fleans.Domain/Activities/EventBasedGateway.cs`:

```csharp
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record EventBasedGateway(string ActivityId) : Gateway(ActivityId)
{
    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        await base.ExecuteAsync(workflowContext, activityContext, definition);
        await activityContext.Complete();
    }

    internal override Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlows = definition.SequenceFlows
            .Where(sf => sf.Source == this)
            .Select(flow => flow.Target)
            .ToList();
        return Task.FromResult(nextFlows);
    }
}
```

**Step 4: Run tests — verify they pass**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~EventBasedGateway" --verbosity minimal`
Expected: 2 tests PASS.

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/EventBasedGateway.cs src/Fleans/Fleans.Domain.Tests/EventBasedGatewayActivityTests.cs
git commit -m "feat: add EventBasedGateway domain activity class with tests"
```

---

## Task 2: Infrastructure — BpmnConverter parsing + test

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs:252` (after parallelGateway parsing)
- Modify: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/BpmnConverterTestBase.cs` (add helper)
- Create: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/EventBasedGatewayTests.cs`

**Step 1: Write the failing test**

Create `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/EventBasedGatewayTests.cs`:

```csharp
using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class EventBasedGatewayTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseEventBasedGateway()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithEventBasedGateway("ebg-workflow", "ebg1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<EventBasedGateway>().FirstOrDefault(g => g.ActivityId == "ebg1");
        Assert.IsNotNull(gateway, "EventBasedGateway should be parsed");
    }
}
```

**Step 2: Run test — verify it fails**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/ --filter "FullyQualifiedName~EventBasedGateway" --verbosity minimal`
Expected: FAIL — `CreateBpmnWithEventBasedGateway` doesn't exist.

**Step 3: Add helper to BpmnConverterTestBase**

Add to the end of `BpmnConverterTestBase.cs` (before the closing `}`):

```csharp
protected static string CreateBpmnWithEventBasedGateway(string processId, string gatewayId)
{
    return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""paymentReceived"" />
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <eventBasedGateway id=""{gatewayId}"" />
    <intermediateCatchEvent id=""timerCatch"">
      <timerEventDefinition><timeDuration>PT1H</timeDuration></timerEventDefinition>
    </intermediateCatchEvent>
    <intermediateCatchEvent id=""msgCatch"">
      <messageEventDefinition messageRef=""msg1"" />
    </intermediateCatchEvent>
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""{gatewayId}"" />
    <sequenceFlow id=""f2"" sourceRef=""{gatewayId}"" targetRef=""timerCatch"" />
    <sequenceFlow id=""f3"" sourceRef=""{gatewayId}"" targetRef=""msgCatch"" />
    <sequenceFlow id=""f4"" sourceRef=""timerCatch"" targetRef=""end1"" />
    <sequenceFlow id=""f5"" sourceRef=""msgCatch"" targetRef=""end2"" />
  </process>
</definitions>";
}
```

**Step 4: Add `<eventBasedGateway>` parsing to BpmnConverter.cs**

In `ParseActivities()`, after the parallel gateway block (after line 252, before `// Parse call activities`), add:

```csharp
// Parse event-based gateways
foreach (var gateway in process.Descendants(Bpmn + "eventBasedGateway"))
{
    var id = GetId(gateway);
    var activity = new EventBasedGateway(id);
    activities.Add(activity);
    activityMap[id] = activity;
}
```

**Step 5: Run test — verify it passes**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/ --filter "FullyQualifiedName~EventBasedGateway" --verbosity minimal`
Expected: 1 test PASS.

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/EventBasedGatewayTests.cs src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/BpmnConverterTestBase.cs
git commit -m "feat: parse <eventBasedGateway> in BpmnConverter"
```

---

## Task 3: Application — CancelEventBasedGatewaySiblings + integration tests

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs:209-232` (CompleteActivityState) and add new method + log messages
- Create: `src/Fleans/Fleans.Application.Tests/EventBasedGatewayTests.cs`

**Step 1: Write the first integration test (message wins, timer cancelled)**

Create `src/Fleans/Fleans.Application.Tests/EventBasedGatewayTests.cs`:

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class EventBasedGatewayTests : WorkflowTestBase
{
    [TestMethod]
    public async Task EventBasedGateway_MessageWins_ShouldCancelTimer()
    {
        // Arrange — Start → Task → EBG → [TimerCatch(1h), MessageCatch] → End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var ebg = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timerCatch",
            new TimerDefinition(TimerType.Duration, "PT1H"));
        var msgDef = new MessageDefinition("msg1", "paymentReceived", "orderId");
        var msgCatch = new MessageIntermediateCatchEvent("msgCatch", "msg1");
        var endTimer = new EndEvent("endTimer");
        var endMsg = new EndEvent("endMsg");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ebg-msg-wins",
            Activities = [start, task, ebg, timerCatch, msgCatch, endTimer, endMsg],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, ebg),
                new SequenceFlow("f3", ebg, timerCatch),
                new SequenceFlow("f4", ebg, msgCatch),
                new SequenceFlow("f5", timerCatch, endTimer),
                new SequenceFlow("f6", msgCatch, endMsg)
            ],
            Messages = [msgDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task with orderId variable (needed for message correlation)
        dynamic vars = new ExpandoObject();
        vars.orderId = "order-ebg-1";
        await workflowInstance.CompleteActivity("task1", vars);

        // Assert — workflow suspended at both catch events
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should be suspended at EBG catch events");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "timerCatch"),
            "Timer catch should be active");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "msgCatch"),
            "Message catch should be active");

        // Act — deliver message (message wins the race)
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("paymentReceived");
        dynamic msgVars = new ExpandoObject();
        msgVars.paymentStatus = "confirmed";
        var delivered = await correlationGrain.DeliverMessage("order-ebg-1", (ExpandoObject)msgVars);

        // Assert — workflow completed via message path
        Assert.IsTrue(delivered, "Message should be delivered");
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");

        // Timer catch should be cancelled
        var timerActivity = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "timerCatch");
        Assert.IsNotNull(timerActivity, "Timer catch should be in completed list");
        Assert.IsTrue(timerActivity.IsCancelled, "Timer catch should be cancelled");

        // Message catch should be completed (not cancelled)
        var msgActivity = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "msgCatch");
        Assert.IsNotNull(msgActivity, "Message catch should be in completed list");
        Assert.IsFalse(msgActivity.IsCancelled, "Message catch should NOT be cancelled");

        // endMsg should be reached (message path), endTimer should NOT
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endMsg"),
            "Message end event should be reached");
    }

    [TestMethod]
    public async Task EventBasedGateway_SignalWins_ShouldCancelTimer()
    {
        // Arrange — Start → EBG → [TimerCatch(1h), SignalCatch] → End
        var start = new StartEvent("start");
        var ebg = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timerCatch",
            new TimerDefinition(TimerType.Duration, "PT1H"));
        var signalDef = new SignalDefinition("sig1", "approvalSignal");
        var signalCatch = new SignalIntermediateCatchEvent("signalCatch", "sig1");
        var endTimer = new EndEvent("endTimer");
        var endSignal = new EndEvent("endSignal");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ebg-signal-wins",
            Activities = [start, ebg, timerCatch, signalCatch, endTimer, endSignal],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, ebg),
                new SequenceFlow("f2", ebg, timerCatch),
                new SequenceFlow("f3", ebg, signalCatch),
                new SequenceFlow("f4", timerCatch, endTimer),
                new SequenceFlow("f5", signalCatch, endSignal)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Assert — workflow suspended at catch events
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted);
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "timerCatch"));
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "signalCatch"));

        // Act — broadcast signal (signal wins the race)
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("approvalSignal");
        await signalGrain.BroadcastSignal();

        // Assert — workflow completed via signal path, timer cancelled
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");

        var timerActivity = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "timerCatch");
        Assert.IsNotNull(timerActivity);
        Assert.IsTrue(timerActivity.IsCancelled, "Timer catch should be cancelled");

        var signalActivity = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "signalCatch");
        Assert.IsNotNull(signalActivity);
        Assert.IsFalse(signalActivity.IsCancelled, "Signal catch should NOT be cancelled");

        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endSignal"),
            "Signal end event should be reached");
    }

    [TestMethod]
    public async Task EventBasedGateway_ThreeWay_MessageWins_ShouldCancelTimerAndSignal()
    {
        // Arrange — Start → Task → EBG → [Timer(1h), Message, Signal] → End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var ebg = new EventBasedGateway("ebg1");
        var timerCatch = new TimerIntermediateCatchEvent("timerCatch",
            new TimerDefinition(TimerType.Duration, "PT1H"));
        var msgDef = new MessageDefinition("msg1", "paymentReceived", "orderId");
        var msgCatch = new MessageIntermediateCatchEvent("msgCatch", "msg1");
        var signalDef = new SignalDefinition("sig1", "approvalSignal");
        var signalCatch = new SignalIntermediateCatchEvent("signalCatch", "sig1");
        var endTimer = new EndEvent("endTimer");
        var endMsg = new EndEvent("endMsg");
        var endSignal = new EndEvent("endSignal");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "ebg-three-way",
            Activities = [start, task, ebg, timerCatch, msgCatch, signalCatch, endTimer, endMsg, endSignal],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, ebg),
                new SequenceFlow("f3", ebg, timerCatch),
                new SequenceFlow("f4", ebg, msgCatch),
                new SequenceFlow("f5", ebg, signalCatch),
                new SequenceFlow("f6", timerCatch, endTimer),
                new SequenceFlow("f7", msgCatch, endMsg),
                new SequenceFlow("f8", signalCatch, endSignal)
            ],
            Messages = [msgDef],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        dynamic vars = new ExpandoObject();
        vars.orderId = "order-3way";
        await workflowInstance.CompleteActivity("task1", vars);

        // Act — deliver message (message wins)
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("paymentReceived");
        await correlationGrain.DeliverMessage("order-3way", new ExpandoObject());

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");

        // Timer and signal should be cancelled
        var timerActivity = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "timerCatch");
        Assert.IsTrue(timerActivity.IsCancelled, "Timer catch should be cancelled");

        var signalActivity = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "signalCatch");
        Assert.IsTrue(signalActivity.IsCancelled, "Signal catch should be cancelled");

        // Message should NOT be cancelled
        var msgActivity = finalSnapshot.CompletedActivities.First(a => a.ActivityId == "msgCatch");
        Assert.IsFalse(msgActivity.IsCancelled, "Message catch should NOT be cancelled");

        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endMsg"),
            "Message end event should be reached");
    }
}
```

**Step 2: Run tests — verify they fail**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~EventBasedGateway" --verbosity minimal`
Expected: FAIL — first test hangs or fails because cancelled activities block the workflow.

**Step 3: Add `IsCancelled` to the query snapshot**

Check if `ActivitySnapshot.IsCancelled` already exists in the query model. If not, it needs to be added to `ActivitySnapshot` and the query mapping. Read `src/Fleans/Fleans.Application/QueryModels/` to verify.

**Step 4: Implement CancelEventBasedGatewaySiblings**

Add to `WorkflowInstance.cs` after `CompleteActivityState` (after line 232):

```csharp
private async Task CancelEventBasedGatewaySiblings(string completedActivityId, IWorkflowDefinition definition)
{
    // Check if this activity is after an Event-Based Gateway
    var incomingFlow = definition.SequenceFlows
        .FirstOrDefault(sf => sf.Target.ActivityId == completedActivityId);
    if (incomingFlow?.Source is not EventBasedGateway gateway) return;

    // Find all sibling catch events (other outgoing flows from the same gateway)
    var siblingActivityIds = definition.SequenceFlows
        .Where(sf => sf.Source == gateway && sf.Target.ActivityId != completedActivityId)
        .Select(sf => sf.Target.ActivityId)
        .ToHashSet();

    foreach (var siblingId in siblingActivityIds)
    {
        var entry = State.GetFirstActive(siblingId);
        if (entry is null) continue;

        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
        var siblingActivity = definition.GetActivity(siblingId);

        // Unsubscribe based on event type
        switch (siblingActivity)
        {
            case TimerIntermediateCatchEvent:
                var callbackGrain = _grainFactory.GetGrain<ITimerCallbackGrain>(
                    this.GetPrimaryKey(), $"{entry.ActivityInstanceId}:{siblingId}");
                await callbackGrain.Cancel();
                break;

            case MessageIntermediateCatchEvent msgCatch:
                var variablesId = await activityInstance.GetVariablesStateId();
                var msgDef = definition.Messages.First(m => m.Id == msgCatch.MessageDefinitionId);
                if (msgDef.CorrelationKeyExpression is not null)
                {
                    var corrValue = await GetVariable(variablesId, msgDef.CorrelationKeyExpression);
                    if (corrValue is not null)
                    {
                        var corrGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(msgDef.Name);
                        await corrGrain.Unsubscribe(corrValue.ToString()!);
                    }
                }
                break;

            case SignalIntermediateCatchEvent sigCatch:
                var sigDef = definition.Signals.First(s => s.Id == sigCatch.SignalDefinitionId);
                var sigGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(sigDef.Name);
                await sigGrain.Unsubscribe(this.GetPrimaryKey(), siblingId);
                break;
        }

        await activityInstance.Cancel("Event-based gateway: sibling event completed");
        LogEventBasedGatewaySiblingCancelled(siblingId, completedActivityId);
    }
}
```

**Step 5: Call it from CompleteActivityState**

In `CompleteActivityState`, after the existing boundary cleanup (after line 231), add:

```csharp
// Cancel sibling catch events if this activity is after an Event-Based Gateway
await CancelEventBasedGatewaySiblings(activityId, definition);
```

**Step 6: Add LoggerMessage**

Add after `LogStatePersistedAfterTransition` (EventId 3006), using EventId 1033 (next in the 1000 WorkflowInstance range):

```csharp
[LoggerMessage(EventId = 1033, Level = LogLevel.Information,
    Message = "Event-based gateway: cancelled sibling {CancelledActivityId} because {WinningActivityId} completed first")]
private partial void LogEventBasedGatewaySiblingCancelled(string cancelledActivityId, string winningActivityId);
```

**Step 7: Run integration tests — verify they pass**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~EventBasedGateway" --verbosity minimal`
Expected: 3 tests PASS.

**Step 8: Run ALL tests to verify no regression**

Run: `dotnet test src/Fleans/ --verbosity minimal`
Expected: All 313+ tests pass.

**Step 9: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs src/Fleans/Fleans.Application.Tests/EventBasedGatewayTests.cs
git commit -m "feat: cancel sibling events when Event-Based Gateway winner fires"
```

---

## Task 4: README update + risk audit update

**Files:**
- Modify: `README.md:49` (Event-Based Gateway row)
- Modify: `docs/plans/2026-02-17-architectural-risk-audit.md:147` (Phase 1.7 checkbox)

**Step 1: Update README**

Change the Event-Based Gateway row from empty to `[x]`:

```
| Event-Based Gateway  | Indicates that the process flow is determined by an event.                   |     [x]     |
```

**Step 2: Update risk audit**

Mark Phase 1.7 as done:

```
- [x] **1.7 — Event-Based Gateway**: Register for multiple events (timer + message + signal), first one to fire completes the gateway, cancel the others. *Done.*
```

**Step 3: Commit**

```bash
git add README.md docs/plans/2026-02-17-architectural-risk-audit.md
git commit -m "docs: mark Event-Based Gateway as implemented (Phase 1 complete)"
```
