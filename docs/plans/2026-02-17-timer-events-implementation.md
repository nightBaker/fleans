# Timer Events Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add Timer Events to the BPMN engine — intermediate catch, boundary, and start event positions with duration/date/cycle timer types.

**Architecture:** Reuse the existing activity-level suspension pattern (IsExecuting = true means "waiting"). Timer activities register Orleans Reminders that call CompleteActivity() when they fire. A separate TimerStartEventScheduler grain handles scheduled workflow creation.

**Tech Stack:** C# / .NET 9, Orleans 9, MSTest, NSubstitute, Orleans.TestingHost

**Design doc:** `docs/plans/2026-02-17-timer-events-design.md`

---

### Task 1: TimerDefinition Value Object

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/TimerDefinition.cs`
- Test: `src/Fleans/Fleans.Domain.Tests/TimerDefinitionTests.cs`

**Step 1: Write the failing tests**

In `src/Fleans/Fleans.Domain.Tests/TimerDefinitionTests.cs`:

```csharp
using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class TimerDefinitionTests
{
    [TestMethod]
    public void GetDueTime_Duration_ShouldReturnTimeSpan()
    {
        var timer = new TimerDefinition(TimerType.Duration, "PT5M");
        var dueTime = timer.GetDueTime();
        Assert.AreEqual(TimeSpan.FromMinutes(5), dueTime);
    }

    [TestMethod]
    public void GetDueTime_Date_ShouldReturnTimeUntilDate()
    {
        var futureDate = DateTimeOffset.UtcNow.AddHours(1);
        var timer = new TimerDefinition(TimerType.Date, futureDate.ToString("o"));
        var dueTime = timer.GetDueTime();
        // Should be approximately 1 hour
        Assert.IsTrue(dueTime > TimeSpan.FromMinutes(59));
        Assert.IsTrue(dueTime < TimeSpan.FromMinutes(61));
    }

    [TestMethod]
    public void GetDueTime_Date_InPast_ShouldReturnZero()
    {
        var pastDate = DateTimeOffset.UtcNow.AddHours(-1);
        var timer = new TimerDefinition(TimerType.Date, pastDate.ToString("o"));
        var dueTime = timer.GetDueTime();
        Assert.AreEqual(TimeSpan.Zero, dueTime);
    }

    [TestMethod]
    public void GetDueTime_Cycle_ShouldReturnInterval()
    {
        var timer = new TimerDefinition(TimerType.Cycle, "R3/PT10M");
        var dueTime = timer.GetDueTime();
        Assert.AreEqual(TimeSpan.FromMinutes(10), dueTime);
    }

    [TestMethod]
    public void ParseCycle_ShouldReturnRepeatCountAndInterval()
    {
        var timer = new TimerDefinition(TimerType.Cycle, "R3/PT10M");
        var (repeatCount, interval) = timer.ParseCycle();
        Assert.AreEqual(3, repeatCount);
        Assert.AreEqual(TimeSpan.FromMinutes(10), interval);
    }

    [TestMethod]
    public void ParseCycle_UnboundedRepeat_ShouldReturnNullCount()
    {
        var timer = new TimerDefinition(TimerType.Cycle, "R/PT1H");
        var (repeatCount, interval) = timer.ParseCycle();
        Assert.IsNull(repeatCount);
        Assert.AreEqual(TimeSpan.FromHours(1), interval);
    }

    [TestMethod]
    public void GetDueTime_Duration_ISO8601_Hours()
    {
        var timer = new TimerDefinition(TimerType.Duration, "PT2H30M");
        var dueTime = timer.GetDueTime();
        Assert.AreEqual(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30), dueTime);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~TimerDefinitionTests" -v n`
Expected: FAIL — TimerDefinition does not exist

**Step 3: Write minimal implementation**

In `src/Fleans/Fleans.Domain/Activities/TimerDefinition.cs`:

```csharp
using System.Xml;

namespace Fleans.Domain.Activities;

public enum TimerType
{
    Duration,
    Date,
    Cycle
}

[GenerateSerializer]
public record TimerDefinition(
    [property: Id(0)] TimerType Type,
    [property: Id(1)] string Expression)
{
    public TimeSpan GetDueTime()
    {
        return Type switch
        {
            TimerType.Duration => XmlConvert.ToTimeSpan(Expression),
            TimerType.Date => GetDateDueTime(),
            TimerType.Cycle => ParseCycle().Interval,
            _ => throw new InvalidOperationException($"Unknown timer type: {Type}")
        };
    }

    private TimeSpan GetDateDueTime()
    {
        var targetDate = DateTimeOffset.Parse(Expression);
        var remaining = targetDate - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public (int? RepeatCount, TimeSpan Interval) ParseCycle()
    {
        // Format: R{count}/{duration} or R/{duration}
        var parts = Expression.Split('/');
        if (parts.Length != 2)
            throw new InvalidOperationException($"Invalid cycle expression: {Expression}. Expected format: R{{count}}/{{duration}}");

        var repeatPart = parts[0]; // "R3" or "R"
        int? repeatCount = repeatPart.Length > 1
            ? int.Parse(repeatPart[1..])
            : null;

        var interval = XmlConvert.ToTimeSpan(parts[1]);
        return (repeatCount, interval);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~TimerDefinitionTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/TimerDefinition.cs src/Fleans/Fleans.Domain.Tests/TimerDefinitionTests.cs
git commit -m "feat: add TimerDefinition value object with duration/date/cycle parsing"
```

---

### Task 2: TimerIntermediateCatchEvent Activity

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/TimerIntermediateCatchEvent.cs`
- Test: `src/Fleans/Fleans.Domain.Tests/TimerIntermediateCatchEventDomainTests.cs`

**Context:** This activity type represents an inline timer in the sequence flow. When executed, it registers a reminder and waits. It does NOT call `activityContext.Complete()` — completion happens externally when the reminder fires.

**Step 1: Write the failing tests**

In `src/Fleans/Fleans.Domain.Tests/TimerIntermediateCatchEventDomainTests.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class TimerIntermediateCatchEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallExecute_AndPublishEvent_ButNotComplete()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var timerEvent = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [timerEvent, end],
            [new SequenceFlow("seq1", timerEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("timer1");

        // Act
        await timerEvent.ExecuteAsync(workflowContext, activityContext);

        // Assert — should execute but NOT complete (waits for reminder)
        await activityContext.Received(1).Execute();
        await activityContext.DidNotReceive().Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("timer1", executedEvent.activityId);
        Assert.AreEqual("TimerIntermediateCatchEvent", executedEvent.TypeName);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var timerEvent = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [timerEvent, end],
            [new SequenceFlow("seq1", timerEvent, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("timer1");

        // Act
        var nextActivities = await timerEvent.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~TimerIntermediateCatchEventDomainTests" -v n`
Expected: FAIL — TimerIntermediateCatchEvent does not exist

**Step 3: Write minimal implementation**

In `src/Fleans/Fleans.Domain/Activities/TimerIntermediateCatchEvent.cs`:

```csharp
namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record TimerIntermediateCatchEvent(
    string ActivityId,
    [property: Id(1)] TimerDefinition TimerDefinition) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        // Call base to publish WorkflowActivityExecutedEvent and mark IsExecuting
        await base.ExecuteAsync(workflowContext, activityContext);
        // Do NOT call activityContext.Complete() — the reminder will do that
    }

    internal override async Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~TimerIntermediateCatchEventDomainTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/TimerIntermediateCatchEvent.cs src/Fleans/Fleans.Domain.Tests/TimerIntermediateCatchEventDomainTests.cs
git commit -m "feat: add TimerIntermediateCatchEvent domain activity"
```

---

### Task 3: BoundaryTimerEvent Activity

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/BoundaryTimerEvent.cs`
- Test: `src/Fleans/Fleans.Domain.Tests/BoundaryTimerEventDomainTests.cs`

**Context:** Mirrors `BoundaryErrorEvent` structure but with a `TimerDefinition`. When fired, it auto-completes (like BoundaryErrorEvent does).

**Step 1: Write the failing tests**

In `src/Fleans/Fleans.Domain.Tests/BoundaryTimerEventDomainTests.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class BoundaryTimerEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundaryTimer, recovery],
            [new SequenceFlow("seq1", boundaryTimer, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("bt1");

        // Act
        await boundaryTimer.ExecuteAsync(workflowContext, activityContext);

        // Assert
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("bt1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundaryTimer, recovery],
            [new SequenceFlow("seq1", boundaryTimer, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("bt1");

        // Act
        var nextActivities = await boundaryTimer.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("recovery", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public void BoundaryTimerEvent_ShouldHaveCorrectProperties()
    {
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundary = new BoundaryTimerEvent("bt1", "task1", timerDef);

        Assert.AreEqual("bt1", boundary.ActivityId);
        Assert.AreEqual("task1", boundary.AttachedToActivityId);
        Assert.AreEqual(TimerType.Duration, boundary.TimerDefinition.Type);
        Assert.AreEqual("PT30M", boundary.TimerDefinition.Expression);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~BoundaryTimerEventDomainTests" -v n`
Expected: FAIL — BoundaryTimerEvent does not exist

**Step 3: Write minimal implementation**

In `src/Fleans/Fleans.Domain/Activities/BoundaryTimerEvent.cs`:

```csharp
namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record BoundaryTimerEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] TimerDefinition TimerDefinition) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        await base.ExecuteAsync(workflowContext, activityContext);
        await activityContext.Complete();
    }

    internal override async Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~BoundaryTimerEventDomainTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/BoundaryTimerEvent.cs src/Fleans/Fleans.Domain.Tests/BoundaryTimerEventDomainTests.cs
git commit -m "feat: add BoundaryTimerEvent domain activity"
```

---

### Task 4: TimerStartEvent Activity

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/TimerStartEvent.cs`
- Test: `src/Fleans/Fleans.Domain.Tests/TimerStartEventDomainTests.cs`

**Context:** Replaces `StartEvent` in workflows that should be triggered on a schedule. Behaves like `StartEvent` during execution (auto-completes and transitions). The scheduling aspect is handled by a separate grain (Task 7).

**Step 1: Write the failing tests**

In `src/Fleans/Fleans.Domain.Tests/TimerStartEventDomainTests.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class TimerStartEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately_LikeStartEvent()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Cycle, "R3/PT10M");
        var timerStart = new TimerStartEvent("timerStart1", timerDef);
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [timerStart, task],
            [new SequenceFlow("seq1", timerStart, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("timerStart1");

        // Act
        await timerStart.ExecuteAsync(workflowContext, activityContext);

        // Assert
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("timerStart1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var timerDef = new TimerDefinition(TimerType.Cycle, "R3/PT10M");
        var timerStart = new TimerStartEvent("timerStart1", timerDef);
        var task = new TaskActivity("task1");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [timerStart, task],
            [new SequenceFlow("seq1", timerStart, task)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("timerStart1");

        // Act
        var nextActivities = await timerStart.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("task1", nextActivities[0].ActivityId);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~TimerStartEventDomainTests" -v n`
Expected: FAIL — TimerStartEvent does not exist

**Step 3: Write minimal implementation**

In `src/Fleans/Fleans.Domain/Activities/TimerStartEvent.cs`:

```csharp
namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record TimerStartEvent(
    string ActivityId,
    [property: Id(1)] TimerDefinition TimerDefinition) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        await base.ExecuteAsync(workflowContext, activityContext);
        await activityContext.Complete();
    }

    internal override async Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~TimerStartEventDomainTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/TimerStartEvent.cs src/Fleans/Fleans.Domain.Tests/TimerStartEventDomainTests.cs
git commit -m "feat: add TimerStartEvent domain activity"
```

---

### Task 5: BpmnConverter — Parse Timer Events from XML

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs` (lines 54-216, ParseActivities method)
- Test: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverterTests.cs`

**Context:** Currently, `ParseActivities` parses all `<boundaryEvent>` as `BoundaryErrorEvent`. We need to:
1. Check for `<timerEventDefinition>` vs `<errorEventDefinition>` inside boundary events
2. Parse `<intermediateCatchEvent>` with `<timerEventDefinition>` as `TimerIntermediateCatchEvent`
3. Parse `<startEvent>` with `<timerEventDefinition>` as `TimerStartEvent` (instead of plain `StartEvent`)

**Step 1: Write the failing tests**

Add to `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverterTests.cs`:

```csharp
[TestMethod]
public async Task ConvertFromXmlAsync_ShouldParseTimerIntermediateCatchEvent_WithDuration()
{
    // Arrange
    var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""timer1"">
      <timerEventDefinition>
        <timeDuration>PT5M</timeDuration>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""timer1"" />
    <sequenceFlow id=""flow2"" sourceRef=""timer1"" targetRef=""end"" />
  </process>
</definitions>";

    // Act
    var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

    // Assert
    var timerEvent = workflow.Activities.OfType<TimerIntermediateCatchEvent>().FirstOrDefault();
    Assert.IsNotNull(timerEvent);
    Assert.AreEqual("timer1", timerEvent.ActivityId);
    Assert.AreEqual(TimerType.Duration, timerEvent.TimerDefinition.Type);
    Assert.AreEqual("PT5M", timerEvent.TimerDefinition.Expression);
}

[TestMethod]
public async Task ConvertFromXmlAsync_ShouldParseTimerIntermediateCatchEvent_WithDate()
{
    var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""timer1"">
      <timerEventDefinition>
        <timeDate>2026-03-01T10:00:00Z</timeDate>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""timer1"" />
    <sequenceFlow id=""flow2"" sourceRef=""timer1"" targetRef=""end"" />
  </process>
</definitions>";

    var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

    var timerEvent = workflow.Activities.OfType<TimerIntermediateCatchEvent>().FirstOrDefault();
    Assert.IsNotNull(timerEvent);
    Assert.AreEqual(TimerType.Date, timerEvent.TimerDefinition.Type);
    Assert.AreEqual("2026-03-01T10:00:00Z", timerEvent.TimerDefinition.Expression);
}

[TestMethod]
public async Task ConvertFromXmlAsync_ShouldParseTimerIntermediateCatchEvent_WithCycle()
{
    var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""timer1"">
      <timerEventDefinition>
        <timeCycle>R3/PT10M</timeCycle>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""timer1"" />
    <sequenceFlow id=""flow2"" sourceRef=""timer1"" targetRef=""end"" />
  </process>
</definitions>";

    var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

    var timerEvent = workflow.Activities.OfType<TimerIntermediateCatchEvent>().FirstOrDefault();
    Assert.IsNotNull(timerEvent);
    Assert.AreEqual(TimerType.Cycle, timerEvent.TimerDefinition.Type);
    Assert.AreEqual("R3/PT10M", timerEvent.TimerDefinition.Expression);
}

[TestMethod]
public async Task ConvertFromXmlAsync_ShouldParseBoundaryTimerEvent()
{
    var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <task id=""task1"" />
    <endEvent id=""end"" />
    <endEvent id=""timeoutEnd"" />
    <boundaryEvent id=""bt1"" attachedToRef=""task1"">
      <timerEventDefinition>
        <timeDuration>PT30M</timeDuration>
      </timerEventDefinition>
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""bt1"" targetRef=""timeoutEnd"" />
  </process>
</definitions>";

    var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

    var boundaryTimer = workflow.Activities.OfType<BoundaryTimerEvent>().FirstOrDefault();
    Assert.IsNotNull(boundaryTimer);
    Assert.AreEqual("bt1", boundaryTimer.ActivityId);
    Assert.AreEqual("task1", boundaryTimer.AttachedToActivityId);
    Assert.AreEqual(TimerType.Duration, boundaryTimer.TimerDefinition.Type);
    Assert.AreEqual("PT30M", boundaryTimer.TimerDefinition.Expression);

    // Should NOT be parsed as BoundaryErrorEvent
    Assert.IsFalse(workflow.Activities.OfType<BoundaryErrorEvent>().Any(b => b.ActivityId == "bt1"));

    // Sequence flow from boundary to timeoutEnd
    var flow = workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == boundaryTimer);
    Assert.IsNotNull(flow);
    Assert.AreEqual("timeoutEnd", flow.Target.ActivityId);
}

[TestMethod]
public async Task ConvertFromXmlAsync_ShouldParseTimerStartEvent()
{
    var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""scheduled-workflow"">
    <startEvent id=""timerStart1"">
      <timerEventDefinition>
        <timeCycle>R/PT1H</timeCycle>
      </timerEventDefinition>
    </startEvent>
    <task id=""task1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""timerStart1"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
  </process>
</definitions>";

    var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

    // Should be TimerStartEvent, not plain StartEvent
    var timerStart = workflow.Activities.OfType<TimerStartEvent>().FirstOrDefault();
    Assert.IsNotNull(timerStart);
    Assert.AreEqual("timerStart1", timerStart.ActivityId);
    Assert.AreEqual(TimerType.Cycle, timerStart.TimerDefinition.Type);
    Assert.AreEqual("R/PT1H", timerStart.TimerDefinition.Expression);
    Assert.IsFalse(workflow.Activities.Any(a => a is StartEvent && a is not TimerStartEvent && a.ActivityId == "timerStart1"));
}

[TestMethod]
public async Task ConvertFromXmlAsync_ShouldStillParseBoundaryErrorEvent_WhenErrorDefinitionPresent()
{
    // Ensure existing BoundaryErrorEvent parsing still works
    var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""error-workflow"">
    <startEvent id=""start"" />
    <task id=""task1"" />
    <endEvent id=""end"" />
    <endEvent id=""errorEnd"" />
    <boundaryEvent id=""err1"" attachedToRef=""task1"">
      <errorEventDefinition errorRef=""500"" />
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""err1"" targetRef=""errorEnd"" />
  </process>
</definitions>";

    var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

    var errorEvent = workflow.Activities.OfType<BoundaryErrorEvent>().FirstOrDefault();
    Assert.IsNotNull(errorEvent);
    Assert.AreEqual("err1", errorEvent.ActivityId);
    Assert.AreEqual("task1", errorEvent.AttachedToActivityId);
    Assert.AreEqual("500", errorEvent.ErrorCode);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/ --filter "FullyQualifiedName~BpmnConverterTests" -v n`
Expected: Some FAIL — new timer types not parsed

**Step 3: Modify BpmnConverter.ParseActivities**

In `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs`, make these changes:

1. **Start events** — check for `<timerEventDefinition>` child:

Replace the start event parsing block (lines 56-63) with:

```csharp
// Parse start events (with optional timer definition)
foreach (var startEvent in process.Descendants(Bpmn + "startEvent"))
{
    var id = GetId(startEvent);
    var timerDef = startEvent.Element(Bpmn + "timerEventDefinition");

    Activity activity;
    if (timerDef != null)
    {
        var timerDefinition = ParseTimerDefinition(timerDef);
        activity = new TimerStartEvent(id, timerDefinition);
    }
    else
    {
        activity = new StartEvent(id);
    }

    activities.Add(activity);
    activityMap[id] = activity;
}
```

2. **Add intermediate catch events** — new block after start events:

```csharp
// Parse intermediate catch events (timer)
foreach (var catchEvent in process.Descendants(Bpmn + "intermediateCatchEvent"))
{
    var id = GetId(catchEvent);
    var timerDef = catchEvent.Element(Bpmn + "timerEventDefinition");

    if (timerDef != null)
    {
        var timerDefinition = ParseTimerDefinition(timerDef);
        var activity = new TimerIntermediateCatchEvent(id, timerDefinition);
        activities.Add(activity);
        activityMap[id] = activity;
    }
}
```

3. **Boundary events** — differentiate timer vs error (replace lines 202-215):

```csharp
// Parse boundary events
foreach (var boundaryEl in process.Descendants(Bpmn + "boundaryEvent"))
{
    var id = GetId(boundaryEl);
    var attachedToRef = boundaryEl.Attribute("attachedToRef")?.Value
        ?? throw new InvalidOperationException($"boundaryEvent '{id}' must have an attachedToRef attribute");

    var timerDef = boundaryEl.Element(Bpmn + "timerEventDefinition");
    var errorDef = boundaryEl.Element(Bpmn + "errorEventDefinition");

    Activity activity;
    if (timerDef != null)
    {
        var timerDefinition = ParseTimerDefinition(timerDef);
        activity = new BoundaryTimerEvent(id, attachedToRef, timerDefinition);
    }
    else
    {
        string? errorCode = errorDef?.Attribute("errorRef")?.Value;
        activity = new BoundaryErrorEvent(id, attachedToRef, errorCode);
    }

    activities.Add(activity);
    activityMap[id] = activity;
}
```

4. **Add helper method** at the end of the class:

```csharp
private static TimerDefinition ParseTimerDefinition(XElement timerEventDef)
{
    var timeDuration = timerEventDef.Element(Bpmn + "timeDuration")?.Value;
    var timeDate = timerEventDef.Element(Bpmn + "timeDate")?.Value;
    var timeCycle = timerEventDef.Element(Bpmn + "timeCycle")?.Value;

    if (timeDuration != null)
        return new TimerDefinition(TimerType.Duration, timeDuration.Trim());
    if (timeDate != null)
        return new TimerDefinition(TimerType.Date, timeDate.Trim());
    if (timeCycle != null)
        return new TimerDefinition(TimerType.Cycle, timeCycle.Trim());

    throw new InvalidOperationException("timerEventDefinition must contain timeDuration, timeDate, or timeCycle");
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/ --filter "FullyQualifiedName~BpmnConverterTests" -v n`
Expected: ALL PASS (both new timer tests and existing boundary error tests)

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs src/Fleans/Fleans.Infrastructure.Tests/BpmnConverterTests.cs
git commit -m "feat: parse timer events in BpmnConverter (intermediate catch, boundary, start)"
```

---

### Task 6: WorkflowInstance Reminder Infrastructure

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs`
- Modify: `src/Fleans/Fleans.Application.Tests/WorkflowTestBase.cs` (add reminder service)
- Test: `src/Fleans/Fleans.Application.Tests/TimerIntermediateCatchEventTests.cs`

**Context:** `WorkflowInstance` grain must implement `IRemindable` to receive reminder callbacks. When the execution loop encounters a `TimerIntermediateCatchEvent`, it registers an Orleans Reminder. When the reminder fires, `ReceiveReminder` calls `CompleteActivity`. For boundary timers, reminders are registered when the attached activity starts executing, and unregistered when the attached activity completes normally.

**Step 1: Write the failing integration tests**

Create `src/Fleans/Fleans.Application.Tests/TimerIntermediateCatchEventTests.cs`:

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class TimerIntermediateCatchEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task TimerIntermediateCatch_ShouldSuspendWorkflow_UntilReminderFires()
    {
        // Arrange — Start → Timer(PT5M) → End
        var start = new StartEvent("start");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var timer = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-test",
            Activities = [start, timer, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timer),
                new SequenceFlow("f2", timer, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — workflow should be suspended at timer (timer is active, not completed)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted, "Workflow should NOT be completed — timer is waiting");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "timer1"),
            "Timer activity should be active");
    }

    [TestMethod]
    public async Task TimerIntermediateCatch_ShouldComplete_WhenReminderSimulated()
    {
        // Arrange — Start → Timer(PT5M) → End
        var start = new StartEvent("start");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
        var timer = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-test",
            Activities = [start, timer, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timer),
                new SequenceFlow("f2", timer, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — simulate reminder firing by calling CompleteActivity
        await workflowInstance.CompleteActivity("timer1", new ExpandoObject());

        // Assert — workflow should now be completed
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed after timer fires");
    }

    [TestMethod]
    public async Task TimerIntermediateCatch_BetweenTasks_ShouldPreserveVariables()
    {
        // Arrange — Start → Task → Timer → End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT1M");
        var timer = new TimerIntermediateCatchEvent("timer1", timerDef);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-vars-test",
            Activities = [start, task, timer, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, timer),
                new SequenceFlow("f3", timer, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete task with variables
        dynamic vars = new ExpandoObject();
        vars.result = "done";
        await workflowInstance.CompleteActivity("task1", vars);

        // Timer is now active — simulate reminder
        await workflowInstance.CompleteActivity("timer1", new ExpandoObject());

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsTrue(snapshot.VariableStates.Count > 0, "Variables should be preserved across timer");
    }
}
```

**Step 2: Run tests to verify they fail (or pass for basic suspension)**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~TimerIntermediateCatchEventTests" -v n`

Note: The basic suspension test should already work because `TimerIntermediateCatchEvent.ExecuteAsync()` doesn't call `Complete()`, so the execution loop will exit with the timer in `IsExecuting = true` state. The `CompleteActivity("timer1", ...)` test should also work because `CompleteActivity` is already a public method. If tests pass, we still need to add reminder registration — but that's the integration with Orleans Reminders.

**Step 3: Add IRemindable to WorkflowInstance**

Modify `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`:

1. Add `IRemindable` to the class declaration:

Change line 12 from:
```csharp
public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain
```
to:
```csharp
public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain, IRemindable
```

2. Add the reminder registration logic. After the `ExecuteWorkflow()` method (after line 61), add:

```csharp
private async Task RegisterTimerReminders()
{
    var definition = await GetWorkflowDefinition();

    foreach (var entry in State.GetActiveActivities().ToList())
    {
        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
        if (!await activityInstance.IsExecuting())
            continue;

        var activity = definition.GetActivity(entry.ActivityId);

        // Register reminder for intermediate catch timer
        if (activity is TimerIntermediateCatchEvent timerCatch)
        {
            var reminderName = $"timer:{entry.ActivityId}";
            var dueTime = timerCatch.TimerDefinition.GetDueTime();
            // Orleans Reminders require minimum 1 minute period; use dueTime for first fire
            await this.RegisterOrUpdateReminder(reminderName, dueTime, TimeSpan.FromMinutes(1));
            LogTimerReminderRegistered(entry.ActivityId, dueTime);
        }

        // Register reminders for boundary timers attached to this activity
        foreach (var boundaryTimer in definition.Activities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == entry.ActivityId))
        {
            var reminderName = $"timer:{boundaryTimer.ActivityId}";
            var dueTime = boundaryTimer.TimerDefinition.GetDueTime();
            await this.RegisterOrUpdateReminder(reminderName, dueTime, TimeSpan.FromMinutes(1));
            LogTimerReminderRegistered(boundaryTimer.ActivityId, dueTime);
        }
    }
}
```

3. Call `RegisterTimerReminders()` at the end of `ExecuteWorkflow()`:

Change `ExecuteWorkflow()` to:
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
            await currentActivity.ExecuteAsync(this, activityState);
        }

        await TransitionToNextActivity();
    }

    await RegisterTimerReminders();
}
```

4. Add the `ReceiveReminder` method:

```csharp
public async Task ReceiveReminder(string reminderName, TickStatus status)
{
    if (!reminderName.StartsWith("timer:"))
        return;

    var activityId = reminderName["timer:".Length..];
    LogTimerReminderFired(activityId);

    // Unregister the reminder first
    var reminder = await this.GetReminder(reminderName);
    if (reminder != null)
        await this.UnregisterReminder(reminder);

    await EnsureWorkflowDefinitionAsync();
    var definition = await GetWorkflowDefinition();

    // Check if this is a boundary timer
    var activity = definition.Activities.FirstOrDefault(a => a.ActivityId == activityId);
    if (activity is BoundaryTimerEvent boundaryTimer)
    {
        await HandleBoundaryTimerFired(boundaryTimer);
    }
    else
    {
        // Intermediate catch timer — just complete the activity
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        await CompleteActivityState(activityId, new System.Dynamic.ExpandoObject());
        await ExecuteWorkflow();
        await _state.WriteStateAsync();
    }
}

private async Task HandleBoundaryTimerFired(BoundaryTimerEvent boundaryTimer)
{
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();
    var attachedActivityId = boundaryTimer.AttachedToActivityId;

    // Check if attached activity is still active
    var attachedEntry = State.GetFirstActive(attachedActivityId);
    if (attachedEntry == null)
        return; // Activity already completed, timer is stale

    // Cancel the attached activity (mark it completed with no variables)
    var attachedInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
    await attachedInstance.Complete();
    State.CompleteEntries([attachedEntry]);
    LogBoundaryTimerInterrupted(boundaryTimer.ActivityId, attachedActivityId);

    // Create and execute boundary timer event instance
    var boundaryInstanceId = Guid.NewGuid();
    var boundaryInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(boundaryInstanceId);
    var variablesId = await attachedInstance.GetVariablesStateId();
    await boundaryInstance.SetActivity(boundaryTimer.ActivityId, boundaryTimer.GetType().Name);
    await boundaryInstance.SetVariablesId(variablesId);

    var boundaryEntry = new ActivityInstanceEntry(boundaryInstanceId, boundaryTimer.ActivityId, State.Id);
    State.AddEntries([boundaryEntry]);

    await boundaryTimer.ExecuteAsync(this, boundaryInstance);
    await TransitionToNextActivity();
    await ExecuteWorkflow();
    await _state.WriteStateAsync();
}
```

5. Add reminder cleanup when an activity completes normally. In `CompleteActivityState` (after line 132), add boundary timer cleanup:

```csharp
private async Task CompleteActivityState(string activityId, ExpandoObject variables)
{
    var entry = State.GetFirstActive(activityId)
        ?? throw new InvalidOperationException("Active activity not found");

    var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
    SetActivityRequestContext(activityId, activityInstance);
    await activityInstance.Complete();
    var variablesId = await activityInstance.GetVariablesStateId();
    RequestContext.Set("VariablesId", variablesId.ToString());

    LogStateMergeState(variablesId);
    State.MergeState(variablesId, variables);

    // Unregister any boundary timer reminders attached to this activity
    await UnregisterBoundaryTimerReminders(activityId);
}
```

6. Add the unregister helper:

```csharp
private async Task UnregisterBoundaryTimerReminders(string activityId)
{
    if (_workflowDefinition == null) return;

    foreach (var boundaryTimer in _workflowDefinition.Activities.OfType<BoundaryTimerEvent>()
        .Where(bt => bt.AttachedToActivityId == activityId))
    {
        var reminderName = $"timer:{boundaryTimer.ActivityId}";
        try
        {
            var reminder = await this.GetReminder(reminderName);
            if (reminder != null)
            {
                await this.UnregisterReminder(reminder);
                LogTimerReminderUnregistered(boundaryTimer.ActivityId);
            }
        }
        catch (Exception)
        {
            // Reminder may not exist — that's fine
        }
    }
}
```

7. Add log messages:

```csharp
[LoggerMessage(EventId = 1017, Level = LogLevel.Information, Message = "Timer reminder registered for activity {ActivityId}, due in {DueTime}")]
private partial void LogTimerReminderRegistered(string activityId, TimeSpan dueTime);

[LoggerMessage(EventId = 1018, Level = LogLevel.Information, Message = "Timer reminder fired for activity {ActivityId}")]
private partial void LogTimerReminderFired(string activityId);

[LoggerMessage(EventId = 1019, Level = LogLevel.Information, Message = "Timer reminder unregistered for activity {ActivityId}")]
private partial void LogTimerReminderUnregistered(string activityId);

[LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Boundary timer {BoundaryTimerId} interrupted attached activity {AttachedActivityId}")]
private partial void LogBoundaryTimerInterrupted(string boundaryTimerId, string attachedActivityId);
```

8. Modify `WorkflowTestBase.cs` to add reminder service. In the `EfCoreSiloConfigurator.Configure` method, add after `AddMemoryGrainStorage("PubSubStore")`:

```csharp
.UseInMemoryReminderService()
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~TimerIntermediateCatchEventTests" -v n`
Expected: PASS

Then run ALL tests to ensure nothing is broken:

Run: `dotnet test src/Fleans/`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs src/Fleans/Fleans.Application.Tests/WorkflowTestBase.cs src/Fleans/Fleans.Application.Tests/TimerIntermediateCatchEventTests.cs
git commit -m "feat: add IRemindable to WorkflowInstance for timer event support"
```

---

### Task 7: BoundaryTimerEvent Integration Tests

**Files:**
- Test: `src/Fleans/Fleans.Application.Tests/BoundaryTimerEventTests.cs`

**Context:** Integration tests that verify boundary timer behavior through the full grain stack.

**Step 1: Write the failing tests**

Create `src/Fleans/Fleans.Application.Tests/BoundaryTimerEventTests.cs`:

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class BoundaryTimerEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task BoundaryTimer_ActivityCompletesFirst_ShouldFollowNormalFlow()
    {
        // Arrange — Start → Task(+BoundaryTimer) → End, BoundaryTimer → TimeoutEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var end = new EndEvent("end");
        var timeoutEnd = new EndEvent("timeoutEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-timer-test",
            Activities = [start, task, boundaryTimer, end, timeoutEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundaryTimer, timeoutEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — task completes before timer fires
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — workflow completes via normal end, not timeout end
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should complete via normal end event");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "timeoutEnd"),
            "Should NOT complete via timeout end event");
    }

    [TestMethod]
    public async Task BoundaryTimer_TimerFiresFirst_ShouldFollowBoundaryFlow()
    {
        // Arrange — Start → Task(+BoundaryTimer) → End, BoundaryTimer → Recovery → TimeoutEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var timerDef = new TimerDefinition(TimerType.Duration, "PT30M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var end = new EndEvent("end");
        var recovery = new TaskActivity("recovery");
        var timeoutEnd = new EndEvent("timeoutEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-timer-fire-test",
            Activities = [start, task, boundaryTimer, end, recovery, timeoutEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundaryTimer, recovery),
                new SequenceFlow("f4", recovery, timeoutEnd)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Verify task is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "task1"));

        // Act — simulate boundary timer firing via ReceiveReminder
        var remindable = workflowInstance.AsReference<IRemindable>();
        await remindable.ReceiveReminder("timer:bt1", default);

        // Assert — should follow boundary path, task1 should be interrupted
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsFalse(snapshot.IsCompleted, "Workflow should NOT be completed yet — recovery task is pending");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "task1"),
            "Original task should be completed (interrupted)");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "recovery"),
            "Recovery task should be active");

        // Complete recovery
        await workflowInstance.CompleteActivity("recovery", new ExpandoObject());

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted);
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "timeoutEnd"),
            "Should complete via timeout end");
    }
}
```

**Step 2: Run tests to verify they pass (implementation was done in Task 6)**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~BoundaryTimerEventTests" -v n`
Expected: PASS (if Task 6 implementation is correct)

If tests fail, debug and fix the HandleBoundaryTimerFired logic.

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/BoundaryTimerEventTests.cs
git commit -m "test: add BoundaryTimerEvent integration tests"
```

---

### Task 8: TimerStartEventScheduler Grain

**Files:**
- Create: `src/Fleans/Fleans.Application/Grains/ITimerStartEventSchedulerGrain.cs`
- Create: `src/Fleans/Fleans.Application/Grains/TimerStartEventSchedulerGrain.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/ProcessDefinitionGrain.cs` (activate scheduler on deploy)
- Test: `src/Fleans/Fleans.Application.Tests/TimerStartEventSchedulerTests.cs`

**Context:** A separate grain keyed by `processDefinitionId` that owns the recurring reminder for TimerStartEvent. On each reminder fire, it creates a new WorkflowInstance and starts it.

**Step 1: Write the failing tests**

Create `src/Fleans/Fleans.Application.Tests/TimerStartEventSchedulerTests.cs`:

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

[TestClass]
public class TimerStartEventSchedulerTests : WorkflowTestBase
{
    [TestMethod]
    public async Task Scheduler_ShouldCreateWorkflowInstance_WhenReminderFires()
    {
        // Arrange — deploy a workflow with TimerStartEvent
        var timerDef = new TimerDefinition(TimerType.Cycle, "R3/PT10M");
        var timerStart = new TimerStartEvent("timerStart1", timerDef);
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "scheduled-workflow",
            Activities = [timerStart, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", timerStart, task),
                new SequenceFlow("f2", task, end)
            ],
            ProcessDefinitionId = "scheduled-process:1:abc"
        };

        // Deploy the process definition
        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow("scheduled-process", workflow);

        // Act — simulate scheduler reminder by calling the scheduler directly
        var scheduler = Cluster.GrainFactory.GetGrain<ITimerStartEventSchedulerGrain>("scheduled-process");
        var createdInstanceId = await scheduler.FireTimerStartEvent();

        // Assert — a workflow instance should have been created and started
        Assert.AreNotEqual(Guid.Empty, createdInstanceId);
        var snapshot = await QueryService.GetStateSnapshot(createdInstanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsStarted);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~TimerStartEventSchedulerTests" -v n`
Expected: FAIL — ITimerStartEventSchedulerGrain does not exist

**Step 3: Write implementation**

Create `src/Fleans/Fleans.Application/Grains/ITimerStartEventSchedulerGrain.cs`:

```csharp
namespace Fleans.Application.Grains;

public interface ITimerStartEventSchedulerGrain : IGrainWithStringKey
{
    Task ActivateScheduler(string processDefinitionId);
    Task DeactivateScheduler();
    Task<Guid> FireTimerStartEvent();
}
```

Create `src/Fleans/Fleans.Application/Grains/TimerStartEventSchedulerGrain.cs`:

```csharp
using Fleans.Application.WorkflowFactory;
using Fleans.Domain.Activities;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class TimerStartEventSchedulerGrain : Grain, ITimerStartEventSchedulerGrain, IRemindable
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<TimerStartEventSchedulerGrain> _logger;
    private string? _processDefinitionId;
    private int _fireCount;
    private int? _maxFireCount;

    public TimerStartEventSchedulerGrain(
        IGrainFactory grainFactory,
        ILogger<TimerStartEventSchedulerGrain> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async Task ActivateScheduler(string processDefinitionId)
    {
        _processDefinitionId = processDefinitionId;

        var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        var definition = await factory.GetLatestWorkflowDefinition(this.GetPrimaryKeyString());
        var timerStart = definition.Activities.OfType<TimerStartEvent>().FirstOrDefault()
            ?? throw new InvalidOperationException("Workflow does not have a TimerStartEvent");

        var dueTime = timerStart.TimerDefinition.GetDueTime();

        if (timerStart.TimerDefinition.Type == TimerType.Cycle)
        {
            var (repeatCount, interval) = timerStart.TimerDefinition.ParseCycle();
            _maxFireCount = repeatCount;
            await this.RegisterOrUpdateReminder("timer-start", dueTime, interval);
        }
        else
        {
            _maxFireCount = 1;
            await this.RegisterOrUpdateReminder("timer-start", dueTime, TimeSpan.FromMinutes(1));
        }

        LogSchedulerActivated(this.GetPrimaryKeyString(), processDefinitionId);
    }

    public async Task DeactivateScheduler()
    {
        try
        {
            var reminder = await this.GetReminder("timer-start");
            if (reminder != null)
                await this.UnregisterReminder(reminder);
        }
        catch { }

        LogSchedulerDeactivated(this.GetPrimaryKeyString());
    }

    public async Task<Guid> FireTimerStartEvent()
    {
        var processKey = this.GetPrimaryKeyString();
        var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        var definition = await factory.GetLatestWorkflowDefinition(processKey);

        var childId = Guid.NewGuid();
        var child = _grainFactory.GetGrain<IWorkflowInstanceGrain>(childId);
        await child.SetWorkflow(definition);
        await child.StartWorkflow();

        _fireCount++;
        LogTimerStartEventFired(processKey, childId, _fireCount);

        return childId;
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != "timer-start")
            return;

        await FireTimerStartEvent();

        // Check if we've reached the max fire count
        if (_maxFireCount.HasValue && _fireCount >= _maxFireCount.Value)
        {
            await DeactivateScheduler();
        }
    }

    [LoggerMessage(EventId = 8000, Level = LogLevel.Information, Message = "Timer start event scheduler activated for process {ProcessKey}, definition {ProcessDefinitionId}")]
    private partial void LogSchedulerActivated(string processKey, string processDefinitionId);

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Timer start event scheduler deactivated for process {ProcessKey}")]
    private partial void LogSchedulerDeactivated(string processKey);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Information, Message = "Timer start event fired for process {ProcessKey}, created instance {InstanceId} (fire #{FireCount})")]
    private partial void LogTimerStartEventFired(string processKey, Guid instanceId, int fireCount);
}
```

**Step 4: Run tests**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~TimerStartEventSchedulerTests" -v n`

Note: This test depends on `IWorkflowInstanceFactoryGrain.DeployWorkflow` and `GetLatestWorkflowDefinition` existing. Check if these exist; if not, the test may need adjustment based on the actual factory grain interface. The test is written to match expected patterns — if deployment doesn't work this way, adjust accordingly.

**Step 5: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/ITimerStartEventSchedulerGrain.cs src/Fleans/Fleans.Application/Grains/TimerStartEventSchedulerGrain.cs src/Fleans/Fleans.Application.Tests/TimerStartEventSchedulerTests.cs
git commit -m "feat: add TimerStartEventScheduler grain for scheduled workflow creation"
```

---

### Task 9: SetWorkflow Support for TimerStartEvent

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs` (SetWorkflow method)

**Context:** Currently `SetWorkflow()` does `workflow.Activities.OfType<StartEvent>().First()` to find the start event. `TimerStartEvent` extends `Activity`, not `StartEvent`. We need to handle both `StartEvent` and `TimerStartEvent` as valid start activities.

**Step 1: Check if this is actually needed**

Look at `SetWorkflow()` line 311. If `TimerStartEvent` inherits from `StartEvent`, this is already handled. Since our design has `TimerStartEvent : Activity` (not `: StartEvent`), we need to fix this.

**Step 2: Write the failing test**

Add to `src/Fleans/Fleans.Application.Tests/TimerStartEventSchedulerTests.cs`:

```csharp
[TestMethod]
public async Task SetWorkflow_ShouldAcceptTimerStartEvent_AsStartActivity()
{
    // Arrange
    var timerDef = new TimerDefinition(TimerType.Duration, "PT5M");
    var timerStart = new TimerStartEvent("timerStart1", timerDef);
    var end = new EndEvent("end");

    var workflow = new WorkflowDefinition
    {
        WorkflowId = "timer-start-workflow",
        Activities = [timerStart, end],
        SequenceFlows = [new SequenceFlow("f1", timerStart, end)]
    };

    var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

    // Act & Assert — should not throw
    await workflowInstance.SetWorkflow(workflow);
    await workflowInstance.StartWorkflow();

    var instanceId = workflowInstance.GetPrimaryKey();
    var snapshot = await QueryService.GetStateSnapshot(instanceId);
    Assert.IsNotNull(snapshot);
    Assert.IsTrue(snapshot.IsCompleted);
}
```

**Step 3: Fix SetWorkflow**

In `WorkflowInstance.cs`, modify `SetWorkflow()` to find either `StartEvent` or `TimerStartEvent`:

Change line 311 from:
```csharp
var startActivity = workflow.Activities.OfType<StartEvent>().First();
```
to:
```csharp
var startActivity = workflow.Activities.FirstOrDefault(a => a is StartEvent or TimerStartEvent)
    ?? throw new InvalidOperationException("Workflow must have a StartEvent or TimerStartEvent");
```

**Step 4: Run tests**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs src/Fleans/Fleans.Application.Tests/TimerStartEventSchedulerTests.cs
git commit -m "fix: support TimerStartEvent as valid start activity in SetWorkflow"
```

---

### Task 10: Update README BPMN Elements Table

**Files:**
- Modify: `README.md`

**Step 1: Update the BPMN elements table**

Find the BPMN elements table in README.md and add rows for the newly supported timer events:

- Timer Intermediate Catch Event — Supported
- Boundary Timer Event — Supported (interrupting only)
- Timer Start Event — Supported

**Step 2: Commit**

```bash
git add README.md
git commit -m "docs: update BPMN elements table with timer event support"
```

---

### Task 11: Final Full Test Suite Run

**Step 1: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS

**Step 2: Run build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded, 0 warnings (or pre-existing warnings only)
