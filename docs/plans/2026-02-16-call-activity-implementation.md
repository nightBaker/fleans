# Call Activity & Boundary Error Events Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement BPMN Call Activity (invoke external process definition as child workflow) with boundary error events for error handling.

**Architecture:** Parent WorkflowInstance grain creates and starts child WorkflowInstance grain directly. Child completion/failure notifications use domain events via Orleans Streams to avoid non-reentrant grain deadlocks. Boundary error events intercept failures in `TransitionToNextActivity()` and route to alternate flows.

**Tech Stack:** Orleans grains, Orleans Streams, EF Core, MSTest + Orleans.TestingHost

**Critical concurrency note:** WorkflowInstance grains are NOT `[Reentrant]`. If parent awaits `child.StartWorkflow()` and the child synchronously calls back to parent (e.g., trivial process StartEvent→EndEvent), it deadlocks. Solution: child publishes `ChildWorkflowCompletedEvent`/`ChildWorkflowFailedEvent` via EventPublisher grain (Orleans Streams). Event handlers then asynchronously call parent. This follows the same pattern as ScriptTask (publishes `ExecuteScriptEvent`, handler calls `CompleteActivity`).

---

### Task 1: Domain Types

**Files:**
- Create: `src/Fleans/Fleans.Domain/VariableMapping.cs`
- Create: `src/Fleans/Fleans.Domain/Activities/CallActivity.cs`
- Create: `src/Fleans/Fleans.Domain/Activities/BoundaryErrorEvent.cs`

**Step 1: Create VariableMapping record**

```csharp
// src/Fleans/Fleans.Domain/VariableMapping.cs
namespace Fleans.Domain;

[GenerateSerializer]
public record VariableMapping(
    [property: Id(0)] string Source,
    [property: Id(1)] string Target);
```

**Step 2: Create CallActivity record**

```csharp
// src/Fleans/Fleans.Domain/Activities/CallActivity.cs
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record CallActivity(
    string ActivityId,
    [property: Id(1)] string CalledProcessKey,
    [property: Id(2)] List<VariableMapping> InputMappings,
    [property: Id(3)] List<VariableMapping> OutputMappings,
    [property: Id(4)] bool PropagateAllParentVariables = true,
    [property: Id(5)] bool PropagateAllChildVariables = true) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        await base.ExecuteAsync(workflowContext, activityContext);
        await workflowContext.StartChildWorkflow(this, activityContext);
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
    }
}
```

**Step 3: Create BoundaryErrorEvent record**

```csharp
// src/Fleans/Fleans.Domain/Activities/BoundaryErrorEvent.cs
namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record BoundaryErrorEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] string? ErrorCode) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        await base.ExecuteAsync(workflowContext, activityContext);
        await activityContext.Complete();
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
    }
}
```

**Step 4: Build to verify compilation**

Run: `dotnet build src/Fleans/Fleans.Domain/`
Expected: Build error — `IWorkflowExecutionContext` doesn't have `StartChildWorkflow` yet. That's OK, we'll add it in Task 3.

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/VariableMapping.cs src/Fleans/Fleans.Domain/Activities/CallActivity.cs src/Fleans/Fleans.Domain/Activities/BoundaryErrorEvent.cs
git commit -m "feat: add CallActivity, BoundaryErrorEvent and VariableMapping domain types"
```

---

### Task 2: State Changes

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs`
- Modify: `src/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs`

**Step 1: Add parent tracking to WorkflowInstanceState**

Add after the `ProcessDefinitionId` property (line 39-40):

```csharp
[Id(11)]
public Guid? ParentWorkflowInstanceId { get; internal set; }

[Id(12)]
public string? ParentActivityId { get; internal set; }
```

**Step 2: Add child tracking to ActivityInstanceEntry**

Add after `IsCompleted` property (line 28-29):

```csharp
[Id(4)]
public Guid? ChildWorkflowInstanceId { get; internal set; }
```

**Step 3: Build to verify**

Run: `dotnet build src/Fleans/Fleans.Domain/`
Expected: PASS

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs src/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs
git commit -m "feat: add parent/child tracking fields to workflow and activity state"
```

---

### Task 3: Interface & Event Changes

**Files:**
- Modify: `src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs`
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/IActivityInstanceGrain.cs`
- Create: `src/Fleans/Fleans.Domain/Events/ChildWorkflowCompletedEvent.cs`
- Create: `src/Fleans/Fleans.Domain/Events/ChildWorkflowFailedEvent.cs`

**Step 1: Add StartChildWorkflow to IWorkflowExecutionContext**

Add to `IWorkflowExecutionContext.cs` after the `Complete()` method:

```csharp
ValueTask StartChildWorkflow(Activities.CallActivity callActivity, IActivityExecutionContext activityContext);
```

Note: needs `using Fleans.Domain.Activities;` or fully qualified name. Use fully qualified since the interface uses minimal imports.

**Step 2: Add methods to IWorkflowInstanceGrain**

Add to `IWorkflowInstanceGrain.cs`:

```csharp
Task SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId);
Task SetInitialVariables(ExpandoObject variables);
Task OnChildWorkflowCompleted(string parentActivityId, ExpandoObject childVariables);
Task OnChildWorkflowFailed(string parentActivityId, Exception exception);
```

**Step 3: Add GetLatestWorkflowDefinition to IWorkflowInstanceFactoryGrain**

Add to `IWorkflowInstanceFactoryGrain.cs`:

```csharp
[ReadOnly]
Task<IWorkflowDefinition> GetLatestWorkflowDefinition(string processDefinitionKey);
```

**Step 4: Add GetErrorState to IActivityInstanceGrain**

Add to `IActivityInstanceGrain.cs`:

```csharp
[ReadOnly]
ValueTask<ActivityErrorState?> GetErrorState();
```

Add `using Fleans.Domain.Errors;` import.

**Step 5: Create ChildWorkflowCompletedEvent**

```csharp
// src/Fleans/Fleans.Domain/Events/ChildWorkflowCompletedEvent.cs
using System.Dynamic;

namespace Fleans.Domain.Events;

[GenerateSerializer]
public record ChildWorkflowCompletedEvent(
    [property: Id(0)] Guid ParentWorkflowInstanceId,
    [property: Id(1)] string ParentActivityId,
    [property: Id(2)] string WorkflowId,
    [property: Id(3)] string? ProcessDefinitionId,
    [property: Id(4)] ExpandoObject ChildVariables) : IDomainEvent;
```

**Step 6: Create ChildWorkflowFailedEvent**

```csharp
// src/Fleans/Fleans.Domain/Events/ChildWorkflowFailedEvent.cs
namespace Fleans.Domain.Events;

[GenerateSerializer]
public record ChildWorkflowFailedEvent(
    [property: Id(0)] Guid ParentWorkflowInstanceId,
    [property: Id(1)] string ParentActivityId,
    [property: Id(2)] string WorkflowId,
    [property: Id(3)] string? ProcessDefinitionId,
    [property: Id(4)] int ErrorCode,
    [property: Id(5)] string ErrorMessage) : IDomainEvent;
```

**Step 7: Build**

Run: `dotnet build src/Fleans/`
Expected: Build errors in `WorkflowInstance.cs` (missing implementations), `WorkflowInstanceFactoryGrain.cs`, `ActivityInstance.cs`. This is expected — implementations come in later tasks.

**Step 8: Commit**

```bash
git add src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs src/Fleans/Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs src/Fleans/Fleans.Application/Grains/IActivityInstanceGrain.cs src/Fleans/Fleans.Domain/Events/ChildWorkflowCompletedEvent.cs src/Fleans/Fleans.Domain/Events/ChildWorkflowFailedEvent.cs
git commit -m "feat: add interfaces and events for Call Activity orchestration"
```

---

### Task 4: CallActivity & BoundaryErrorEvent Unit Tests

**Files:**
- Create: `src/Fleans/Fleans.Domain.Tests/CallActivityDomainTests.cs`
- Create: `src/Fleans/Fleans.Domain.Tests/BoundaryErrorEventDomainTests.cs`

**Step 1: Write CallActivity unit tests**

```csharp
// src/Fleans/Fleans.Domain.Tests/CallActivityDomainTests.cs
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class CallActivityDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallStartChildWorkflow()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "childProcess",
            [new VariableMapping("orderId", "orderId")],
            [new VariableMapping("result", "result")],
            propagateAllParentVariables: false,
            propagateAllChildVariables: false);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [callActivity, end],
            [new SequenceFlow("seq1", callActivity, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("call1");

        // Act
        await callActivity.ExecuteAsync(workflowContext, activityContext);

        // Assert
        await activityContext.Received(1).Execute();
        await workflowContext.Received(1).StartChildWorkflow(callActivity, activityContext);
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("call1", executedEvent.activityId);
        Assert.AreEqual("CallActivity", executedEvent.TypeName);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnSingleTarget_ViaSequenceFlow()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "childProcess", [], []);
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [callActivity, end],
            [new SequenceFlow("seq1", callActivity, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("call1");

        // Act
        var nextActivities = await callActivity.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmpty_WhenNoOutgoingFlow()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "childProcess", [], []);
        var definition = ActivityTestHelper.CreateWorkflowDefinition([callActivity], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("call1");

        // Act
        var nextActivities = await callActivity.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(0, nextActivities);
    }
}
```

**Step 2: Write BoundaryErrorEvent unit tests**

```csharp
// src/Fleans/Fleans.Domain.Tests/BoundaryErrorEventDomainTests.cs
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class BoundaryErrorEventDomainTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteImmediately()
    {
        // Arrange
        var boundaryEvent = new BoundaryErrorEvent("err1", "call1", null);
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundaryEvent, recovery],
            [new SequenceFlow("seq1", boundaryEvent, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, publishedEvents) = ActivityTestHelper.CreateActivityContext("err1");

        // Act
        await boundaryEvent.ExecuteAsync(workflowContext, activityContext);

        // Assert
        await activityContext.Received(1).Execute();
        await activityContext.Received(1).Complete();
        var executedEvent = publishedEvents.OfType<WorkflowActivityExecutedEvent>().Single();
        Assert.AreEqual("err1", executedEvent.activityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnTarget_ViaSequenceFlow()
    {
        // Arrange
        var boundaryEvent = new BoundaryErrorEvent("err1", "call1", null);
        var recovery = new TaskActivity("recovery");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [boundaryEvent, recovery],
            [new SequenceFlow("seq1", boundaryEvent, recovery)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("err1");

        // Act
        var nextActivities = await boundaryEvent.GetNextActivities(workflowContext, activityContext);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("recovery", nextActivities[0].ActivityId);
    }
}
```

**Step 3: Add NSubstitute setup for StartChildWorkflow**

In `src/Fleans/Fleans.Domain.Tests/ActivityTestHelper.cs`, add to `CreateWorkflowContext()`:

```csharp
context.StartChildWorkflow(Arg.Any<Activities.CallActivity>(), Arg.Any<IActivityExecutionContext>())
    .Returns(ValueTask.CompletedTask);
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/`
Expected: All tests PASS (activity behavior already implemented in Task 1).

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain.Tests/CallActivityDomainTests.cs src/Fleans/Fleans.Domain.Tests/BoundaryErrorEventDomainTests.cs src/Fleans/Fleans.Domain.Tests/ActivityTestHelper.cs
git commit -m "test: add CallActivity and BoundaryErrorEvent unit tests"
```

---

### Task 5: BpmnConverter Parsing + Tests

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs`
- Modify: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverterTests.cs`

**Step 1: Write failing BpmnConverter tests**

Add to `BpmnConverterTests.cs`:

```csharp
[TestMethod]
public async Task ConvertFromXmlAsync_ShouldParseCallActivity_WithMappings()
{
    // Arrange
    var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"">
      <extensionElements>
        <inputMapping source=""orderId"" target=""orderId"" />
        <inputMapping source=""amount"" target=""paymentAmount"" />
        <outputMapping source=""transactionId"" target=""txId"" />
      </extensionElements>
    </callActivity>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
  </process>
</definitions>";

    // Act
    var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

    // Assert
    var callActivity = workflow.Activities.OfType<CallActivity>().FirstOrDefault();
    Assert.IsNotNull(callActivity);
    Assert.AreEqual("call1", callActivity.ActivityId);
    Assert.AreEqual("childProcess", callActivity.CalledProcessKey);
    Assert.AreEqual(2, callActivity.InputMappings.Count);
    Assert.AreEqual("orderId", callActivity.InputMappings[0].Source);
    Assert.AreEqual("orderId", callActivity.InputMappings[0].Target);
    Assert.AreEqual("amount", callActivity.InputMappings[1].Source);
    Assert.AreEqual("paymentAmount", callActivity.InputMappings[1].Target);
    Assert.AreEqual(1, callActivity.OutputMappings.Count);
    Assert.AreEqual("transactionId", callActivity.OutputMappings[0].Source);
    Assert.AreEqual("txId", callActivity.OutputMappings[0].Target);
}

[TestMethod]
public async Task ConvertFromXmlAsync_ShouldParseCallActivity_WithNoMappings()
{
    // Arrange
    var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
  </process>
</definitions>";

    // Act
    var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

    // Assert
    var callActivity = workflow.Activities.OfType<CallActivity>().FirstOrDefault();
    Assert.IsNotNull(callActivity);
    Assert.AreEqual("childProcess", callActivity.CalledProcessKey);
    Assert.AreEqual(0, callActivity.InputMappings.Count);
    Assert.AreEqual(0, callActivity.OutputMappings.Count);
}

[TestMethod]
public async Task ConvertFromXmlAsync_ShouldParseBoundaryErrorEvent_WithErrorCode()
{
    // Arrange
    var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" />
    <endEvent id=""end"" />
    <endEvent id=""errorEnd"" />
    <boundaryEvent id=""err1"" attachedToRef=""call1"">
      <errorEventDefinition errorRef=""PaymentFailed"" />
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""err1"" targetRef=""errorEnd"" />
  </process>
</definitions>";

    // Act
    var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

    // Assert
    var boundaryEvent = workflow.Activities.OfType<BoundaryErrorEvent>().FirstOrDefault();
    Assert.IsNotNull(boundaryEvent);
    Assert.AreEqual("err1", boundaryEvent.ActivityId);
    Assert.AreEqual("call1", boundaryEvent.AttachedToActivityId);
    Assert.AreEqual("PaymentFailed", boundaryEvent.ErrorCode);

    // Sequence flow from boundary event to errorEnd
    var flow = workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == boundaryEvent);
    Assert.IsNotNull(flow);
    Assert.AreEqual("errorEnd", flow.Target.ActivityId);
}

[TestMethod]
public async Task ConvertFromXmlAsync_ShouldParseBoundaryErrorEvent_CatchAll_WhenNoErrorRef()
{
    // Arrange
    var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" />
    <endEvent id=""end"" />
    <endEvent id=""errorEnd"" />
    <boundaryEvent id=""err1"" attachedToRef=""call1"">
      <errorEventDefinition />
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""err1"" targetRef=""errorEnd"" />
  </process>
</definitions>";

    // Act
    var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

    // Assert
    var boundaryEvent = workflow.Activities.OfType<BoundaryErrorEvent>().FirstOrDefault();
    Assert.IsNotNull(boundaryEvent);
    Assert.IsNull(boundaryEvent.ErrorCode);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/ --filter "CallActivity|BoundaryErrorEvent"`
Expected: FAIL — BpmnConverter doesn't parse `callActivity` or `boundaryEvent` yet.

**Step 3: Implement callActivity and boundaryEvent parsing**

In `BpmnConverter.cs`, add to `ParseActivities()` method, after the existing parallel gateway parsing:

```csharp
// Parse call activities
foreach (var callActivityEl in process.Descendants(Bpmn + "callActivity"))
{
    var id = GetId(callActivityEl);
    var calledElement = callActivityEl.Attribute("calledElement")?.Value
        ?? throw new InvalidOperationException($"callActivity '{id}' must have a calledElement attribute");

    var propagateAllParent = ParseBoolAttribute(callActivityEl, "propagateAllParentVariables", true);
    var propagateAllChild = ParseBoolAttribute(callActivityEl, "propagateAllChildVariables", true);

    var inputMappings = new List<VariableMapping>();
    var outputMappings = new List<VariableMapping>();

    var extensionElements = callActivityEl.Element(Bpmn + "extensionElements");
    if (extensionElements != null)
    {
        foreach (var input in extensionElements.Elements("inputMapping"))
        {
            var source = input.Attribute("source")?.Value ?? "";
            var target = input.Attribute("target")?.Value ?? "";
            inputMappings.Add(new VariableMapping(source, target));
        }

        foreach (var output in extensionElements.Elements("outputMapping"))
        {
            var source = output.Attribute("source")?.Value ?? "";
            var target = output.Attribute("target")?.Value ?? "";
            outputMappings.Add(new VariableMapping(source, target));
        }
    }

    var activity = new CallActivity(id, calledElement, inputMappings, outputMappings, propagateAllParent, propagateAllChild);
    activities.Add(activity);
    activityMap[id] = activity;
}
```

Also add this helper method to BpmnConverter:

```csharp
private static bool ParseBoolAttribute(XElement element, string attributeName, bool defaultValue)
{
    var attr = element.Attribute(attributeName)?.Value;
    return attr is not null ? bool.Parse(attr) : defaultValue;
}

// Parse boundary events
foreach (var boundaryEl in process.Descendants(Bpmn + "boundaryEvent"))
{
    var id = GetId(boundaryEl);
    var attachedToRef = boundaryEl.Attribute("attachedToRef")?.Value
        ?? throw new InvalidOperationException($"boundaryEvent '{id}' must have an attachedToRef attribute");

    var errorDef = boundaryEl.Element(Bpmn + "errorEventDefinition");
    string? errorCode = errorDef?.Attribute("errorRef")?.Value;

    var activity = new BoundaryErrorEvent(id, attachedToRef, errorCode);
    activities.Add(activity);
    activityMap[id] = activity;
}
```

Note: `inputMapping`/`outputMapping` elements are NOT in the BPMN namespace — they're custom extension elements, so use `Elements("inputMapping")` not `Elements(Bpmn + "inputMapping")`.

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/`
Expected: All tests PASS.

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs src/Fleans/Fleans.Infrastructure.Tests/BpmnConverterTests.cs
git commit -m "feat: add BPMN parsing for callActivity and boundaryEvent elements"
```

---

### Task 6: Grain Implementations — Factory, ActivityInstance, WorkflowInstance

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/ActivityInstance.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`

**Step 1: Implement GetLatestWorkflowDefinition on factory grain**

Add to `WorkflowInstanceFactoryGrain.cs`:

```csharp
public Task<IWorkflowDefinition> GetLatestWorkflowDefinition(string processDefinitionKey)
{
    var definition = GetLatestDefinitionOrThrow(processDefinitionKey);
    return Task.FromResult<IWorkflowDefinition>(definition.Workflow);
}
```

**Step 2: Implement GetErrorState on ActivityInstance grain**

Add to `ActivityInstance.cs`:

```csharp
public ValueTask<ActivityErrorState?> GetErrorState()
    => ValueTask.FromResult(State.ErrorState);
```

Add `using Fleans.Domain.Errors;` import.

**Step 3: Implement SetParentInfo on WorkflowInstance**

Add to `WorkflowInstance.cs`:

```csharp
public async Task SetParentInfo(Guid parentWorkflowInstanceId, string parentActivityId)
{
    State.ParentWorkflowInstanceId = parentWorkflowInstanceId;
    State.ParentActivityId = parentActivityId;
    LogParentInfoSet(parentWorkflowInstanceId, parentActivityId);
    await _state.WriteStateAsync();
}
```

Add log message:

```csharp
[LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Parent info set: ParentWorkflowInstanceId={ParentWorkflowInstanceId}, ParentActivityId={ParentActivityId}")]
private partial void LogParentInfoSet(Guid parentWorkflowInstanceId, string parentActivityId);
```

**Step 4: Implement SetInitialVariables on WorkflowInstance**

```csharp
public async Task SetInitialVariables(ExpandoObject variables)
{
    if (State.VariableStates.Count == 0)
        throw new InvalidOperationException("Call SetWorkflow before SetInitialVariables.");

    State.MergeState(State.VariableStates[0].Id, variables);
    LogInitialVariablesSet();
    await _state.WriteStateAsync();
}
```

Add log message:

```csharp
[LoggerMessage(EventId = 1012, Level = LogLevel.Debug, Message = "Initial variables set")]
private partial void LogInitialVariablesSet();
```

**Step 5: Implement StartChildWorkflow on WorkflowInstance**

```csharp
public async ValueTask StartChildWorkflow(Activities.CallActivity callActivity, IActivityExecutionContext activityContext)
{
    // Resolve latest definition for the called process
    var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
    var childDefinition = await factory.GetLatestWorkflowDefinition(callActivity.CalledProcessKey);

    // Create child workflow instance
    var childId = Guid.NewGuid();
    var child = _grainFactory.GetGrain<IWorkflowInstanceGrain>(childId);

    LogStartingChildWorkflow(callActivity.CalledProcessKey, childId);

    await child.SetWorkflow(childDefinition);
    await child.SetParentInfo(this.GetPrimaryKey(), callActivity.ActivityId);

    // Map input variables
    var activityInstanceId = await activityContext.GetActivityInstanceId();
    var activityGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(activityInstanceId);
    var variablesId = await activityGrain.GetVariablesStateId();
    var parentVariables = State.GetVariableState(variablesId).Variables;
    var childInputVars = BuildChildInputVariables(callActivity, parentVariables);

    if (((IDictionary<string, object?>)childInputVars).Count > 0)
        await child.SetInitialVariables(childInputVars);

    // Record child reference on parent's activity entry
    var entry = State.GetFirstActive(callActivity.ActivityId)
        ?? throw new InvalidOperationException($"Active entry not found for '{callActivity.ActivityId}'");
    entry.ChildWorkflowInstanceId = childId;

    // Start child — this is safe because child completion uses domain events
    // (ChildWorkflowCompletedEvent), not direct grain callbacks
    await child.StartWorkflow();

    await _state.WriteStateAsync();
}

private static ExpandoObject BuildChildInputVariables(Activities.CallActivity callActivity, ExpandoObject parentVariables)
{
    var result = new ExpandoObject();
    var sourceDict = (IDictionary<string, object?>)parentVariables;
    var resultDict = (IDictionary<string, object?>)result;

    // If propagateAllParentVariables, copy all parent variables first
    if (callActivity.PropagateAllParentVariables)
    {
        foreach (var kvp in sourceDict)
            resultDict[kvp.Key] = kvp.Value;
    }

    // Apply input mappings (rename/select specific variables)
    foreach (var mapping in callActivity.InputMappings)
    {
        if (sourceDict.TryGetValue(mapping.Source, out var value))
            resultDict[mapping.Target] = value;
    }

    return result;
}

private static ExpandoObject BuildParentOutputVariables(Activities.CallActivity callActivity, ExpandoObject childVariables)
{
    var result = new ExpandoObject();
    var sourceDict = (IDictionary<string, object?>)childVariables;
    var resultDict = (IDictionary<string, object?>)result;

    // If propagateAllChildVariables, copy all child variables first
    if (callActivity.PropagateAllChildVariables)
    {
        foreach (var kvp in sourceDict)
            resultDict[kvp.Key] = kvp.Value;
    }

    // Apply output mappings (rename/select specific variables)
    foreach (var mapping in callActivity.OutputMappings)
    {
        if (sourceDict.TryGetValue(mapping.Source, out var value))
            resultDict[mapping.Target] = value;
    }

    return result;
}
```

Add log message:

```csharp
[LoggerMessage(EventId = 1013, Level = LogLevel.Information, Message = "Starting child workflow: CalledProcessKey={CalledProcessKey}, ChildId={ChildId}")]
private partial void LogStartingChildWorkflow(string calledProcessKey, Guid childId);
```

**Step 6: Implement OnChildWorkflowCompleted**

```csharp
public async Task OnChildWorkflowCompleted(string parentActivityId, ExpandoObject childVariables)
{
    await EnsureWorkflowDefinitionAsync();
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();
    LogChildWorkflowCompleted(parentActivityId);

    var definition = await GetWorkflowDefinition();
    var callActivity = definition.GetActivity(parentActivityId) as Activities.CallActivity
        ?? throw new InvalidOperationException($"Activity '{parentActivityId}' is not a CallActivity");

    var mappedOutput = BuildParentOutputVariables(callActivity, childVariables);
    await CompleteActivityState(parentActivityId, mappedOutput);
    await ExecuteWorkflow();
    await _state.WriteStateAsync();
}
```

Add log message:

```csharp
[LoggerMessage(EventId = 1014, Level = LogLevel.Information, Message = "Child workflow completed for CallActivity {ParentActivityId}")]
private partial void LogChildWorkflowCompleted(string parentActivityId);
```

**Step 7: Implement OnChildWorkflowFailed**

```csharp
public async Task OnChildWorkflowFailed(string parentActivityId, Exception exception)
{
    await EnsureWorkflowDefinitionAsync();
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();
    LogChildWorkflowFailed(parentActivityId);

    await FailActivityWithBoundaryCheck(parentActivityId, exception);
    await _state.WriteStateAsync();
}
```

Add log message:

```csharp
[LoggerMessage(EventId = 1015, Level = LogLevel.Warning, Message = "Child workflow failed for CallActivity {ParentActivityId}")]
private partial void LogChildWorkflowFailed(string parentActivityId);
```

**Step 8: Implement boundary error event routing in FailActivity**

Modify the existing `FailActivity` method to use the new boundary check:

```csharp
public async Task FailActivity(string activityId, Exception exception)
{
    await EnsureWorkflowDefinitionAsync();
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();
    LogFailingActivity(activityId);

    await FailActivityWithBoundaryCheck(activityId, exception);
    await _state.WriteStateAsync();
}

private async Task FailActivityWithBoundaryCheck(string activityId, Exception exception)
{
    await FailActivityState(activityId, exception);

    // Check for boundary error event
    var definition = await GetWorkflowDefinition();
    var activityEntry = State.GetFirstActive(activityId) ?? State.Entries.Last(e => e.ActivityId == activityId);
    var activityGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(activityEntry.ActivityInstanceId);
    var errorState = await activityGrain.GetErrorState();

    if (errorState is not null)
    {
        var boundaryEvent = definition.Activities
            .OfType<Activities.BoundaryErrorEvent>()
            .FirstOrDefault(b => b.AttachedToActivityId == activityId
                && (b.ErrorCode == null || b.ErrorCode == errorState.Code.ToString()));

        if (boundaryEvent is not null)
        {
            LogBoundaryEventTriggered(boundaryEvent.ActivityId, activityId);

            // Create entry for boundary event
            var boundaryInstanceId = Guid.NewGuid();
            var boundaryInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(boundaryInstanceId);
            var variablesId = await activityGrain.GetVariablesStateId();
            await boundaryInstance.SetActivity(boundaryEvent.ActivityId, boundaryEvent.GetType().Name);
            await boundaryInstance.SetVariablesId(variablesId);

            var boundaryEntry = new ActivityInstanceEntry(boundaryInstanceId, boundaryEvent.ActivityId, State.Id);
            State.AddEntries([boundaryEntry]);

            // Execute boundary event (completes immediately, routes to recovery path)
            await boundaryEvent.ExecuteAsync(this, boundaryInstance);
            await TransitionToNextActivity();
            return;
        }
    }

    await ExecuteWorkflow();
}
```

Add log message:

```csharp
[LoggerMessage(EventId = 1016, Level = LogLevel.Information, Message = "Boundary error event {BoundaryEventId} triggered by failed activity {ActivityId}")]
private partial void LogBoundaryEventTriggered(string boundaryEventId, string activityId);
```

**Step 9: Modify Complete() to publish child completion event**

Update the existing `Complete()` method:

```csharp
public async ValueTask Complete()
{
    State.Complete();
    State.CompletedAt = DateTimeOffset.UtcNow;
    LogStateCompleted();
    await _state.WriteStateAsync();

    // Notify parent if this is a child workflow
    if (State.ParentWorkflowInstanceId.HasValue)
    {
        var childVariables = State.VariableStates.Count > 0
            ? State.VariableStates[0].Variables
            : new ExpandoObject();

        var eventPublisher = _grainFactory.GetGrain<IEventPublisher>(0);
        await eventPublisher.Publish(new ChildWorkflowCompletedEvent(
            State.ParentWorkflowInstanceId.Value,
            State.ParentActivityId!,
            _workflowDefinition!.WorkflowId,
            _workflowDefinition.ProcessDefinitionId,
            childVariables));
    }
}
```

Add `using Fleans.Domain.Events;` and `using System.Dynamic;` if not already present.

**Step 10: Build**

Run: `dotnet build src/Fleans/`
Expected: PASS (or minor fixups needed).

**Step 11: Commit**

```bash
git add src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs src/Fleans/Fleans.Application/Grains/ActivityInstance.cs src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "feat: implement Call Activity grain orchestration with boundary error events"
```

---

### Task 7: Event Handlers

**Files:**
- Modify: `src/Fleans/Fleans.Application/Events/WorkflowEventsPublisher.cs`
- Create: `src/Fleans/Fleans.Application/Events/Handlers/ChildWorkflowCompletedEventHandler.cs`
- Create: `src/Fleans/Fleans.Application/Events/Handlers/IChildWorkflowCompletedEventHandler.cs`

**Step 1: Register ChildWorkflowCompletedEvent in WorkflowEventsPublisher**

Add to the `switch` in `Publish()` method, before the `default` case:

```csharp
case ChildWorkflowCompletedEvent childCompletedEvent:
    var childCompletedStreamId = StreamId.Create(StreamNameSpace, nameof(ChildWorkflowCompletedEvent));
    var childCompletedStream = _streamProvider.GetStream<ChildWorkflowCompletedEvent>(childCompletedStreamId);
    await childCompletedStream.OnNextAsync(childCompletedEvent);
    break;
```

Add `using Fleans.Domain.Events;` if not already present.

**Step 2: Create handler interface**

```csharp
// src/Fleans/Fleans.Application/Events/Handlers/IChildWorkflowCompletedEventHandler.cs
namespace Fleans.Application.Events.Handlers;

public interface IChildWorkflowCompletedEventHandler : IGrainWithStringKey
{
}
```

**Step 3: Create handler implementation**

```csharp
// src/Fleans/Fleans.Application/Events/Handlers/ChildWorkflowCompletedEventHandler.cs
using Fleans.Application.Grains;
using Fleans.Application.Logging;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Fleans.Application.Events.Handlers;

[ImplicitStreamSubscription(WorkflowEventsPublisher.StreamNameSpace)]
public partial class ChildWorkflowCompletedEventHandler : Grain, IChildWorkflowCompletedEventHandler, IAsyncObserver<ChildWorkflowCompletedEvent>
{
    private readonly ILogger<ChildWorkflowCompletedEventHandler> _logger;
    private readonly IGrainFactory _grainFactory;

    public ChildWorkflowCompletedEventHandler(ILogger<ChildWorkflowCompletedEventHandler> logger, IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventsPublisher.StreamProvider);
        var streamId = StreamId.Create(WorkflowEventsPublisher.StreamNameSpace, nameof(ChildWorkflowCompletedEvent));
        var stream = streamProvider.GetStream<ChildWorkflowCompletedEvent>(streamId);
        await stream.SubscribeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task OnNextAsync(ChildWorkflowCompletedEvent item, StreamSequenceToken? token = null)
    {
        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            _logger, item.WorkflowId, item.ProcessDefinitionId, item.ParentWorkflowInstanceId, item.ParentActivityId);

        LogHandlingChildCompleted(item.ParentActivityId, item.ParentWorkflowInstanceId);

        var parentWorkflow = _grainFactory.GetGrain<IWorkflowInstanceGrain>(item.ParentWorkflowInstanceId);
        await parentWorkflow.OnChildWorkflowCompleted(item.ParentActivityId, item.ChildVariables);
    }

    public Task OnCompletedAsync()
    {
        LogStreamCompleted();
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        LogStreamError(ex);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 4020, Level = LogLevel.Information, Message = "Handling child workflow completed for activity {ActivityId} on parent {ParentWorkflowInstanceId}")]
    private partial void LogHandlingChildCompleted(string activityId, Guid parentWorkflowInstanceId);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Information, Message = "Child workflow completed event stream completed")]
    private partial void LogStreamCompleted();

    [LoggerMessage(EventId = 4022, Level = LogLevel.Error, Message = "Child workflow completed event stream error")]
    private partial void LogStreamError(Exception ex);
}
```

**Step 4: Build**

Run: `dotnet build src/Fleans/`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application/Events/WorkflowEventsPublisher.cs src/Fleans/Fleans.Application/Events/Handlers/ChildWorkflowCompletedEventHandler.cs src/Fleans/Fleans.Application/Events/Handlers/IChildWorkflowCompletedEventHandler.cs
git commit -m "feat: add ChildWorkflowCompletedEvent handler for parent notification via Orleans Streams"
```

---

### Task 8: Integration Tests

**Files:**
- Create: `src/Fleans/Fleans.Application.Tests/CallActivityTests.cs`

**Step 1: Write integration tests**

```csharp
// src/Fleans/Fleans.Application.Tests/CallActivityTests.cs
using Fleans.Application.Grains;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class CallActivityTests : WorkflowTestBase
{
    [TestMethod]
    public async Task CallActivity_ShouldCompleteParent_WhenChildCompletes()
    {
        // Arrange — deploy child process: start → task → end
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");
        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cf1", childStart, childTask),
                new SequenceFlow("cf2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Arrange — parent process: start → callActivity → end
        var parentStart = new StartEvent("parentStart");
        var callActivity = new CallActivity("call1", "childProcess", [], []);
        var parentEnd = new EndEvent("parentEnd");
        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess",
            Activities = [parentStart, callActivity, parentEnd],
            SequenceFlows =
            [
                new SequenceFlow("pf1", parentStart, callActivity),
                new SequenceFlow("pf2", callActivity, parentEnd)
            ]
        };

        var parentInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await parentInstance.SetWorkflow(parentWorkflow);
        await parentInstance.StartWorkflow();

        // Act — complete child task (triggers child EndEvent → parent callback)
        // Find the child workflow instance from parent state
        var parentId = parentInstance.GetPrimaryKey();
        var parentSnapshot = await QueryService.GetStateSnapshot(parentId);
        Assert.IsNotNull(parentSnapshot);

        // The call activity should be active with a child reference
        var callEntry = parentSnapshot.ActiveActivities.FirstOrDefault(a => a.ActivityId == "call1");
        Assert.IsNotNull(callEntry, "Call activity should be active");

        // Complete child task
        var childInstanceId = callEntry.ChildWorkflowInstanceId;
        Assert.IsNotNull(childInstanceId, "Child workflow instance ID should be set");
        var childInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(childInstanceId.Value);
        await childInstance.CompleteActivity("childTask", new ExpandoObject());

        // Allow event propagation
        await Task.Delay(500);

        // Assert — parent should be completed
        var finalSnapshot = await QueryService.GetStateSnapshot(parentId);
        Assert.IsNotNull(finalSnapshot);
        Assert.IsTrue(finalSnapshot.IsCompleted, "Parent workflow should be completed");
    }

    [TestMethod]
    public async Task CallActivity_ShouldMapInputVariables_ToChild()
    {
        // Arrange — deploy child process
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");
        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess2",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cf1", childStart, childTask),
                new SequenceFlow("cf2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Parent with input mappings: orderId→orderId, amount→paymentAmount
        var parentStart = new StartEvent("parentStart");
        var parentTask = new TaskActivity("parentTask");
        var callActivity = new CallActivity("call1", "childProcess2",
            [new VariableMapping("orderId", "orderId"), new VariableMapping("amount", "paymentAmount")],
            []);
        var parentEnd = new EndEvent("parentEnd");
        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess2",
            Activities = [parentStart, parentTask, callActivity, parentEnd],
            SequenceFlows =
            [
                new SequenceFlow("pf1", parentStart, parentTask),
                new SequenceFlow("pf2", parentTask, callActivity),
                new SequenceFlow("pf3", callActivity, parentEnd)
            ]
        };

        var parentInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await parentInstance.SetWorkflow(parentWorkflow);
        await parentInstance.StartWorkflow();

        // Complete parent task with variables
        dynamic parentVars = new ExpandoObject();
        parentVars.orderId = 42;
        parentVars.amount = 100;
        parentVars.secret = "should-not-pass";
        await parentInstance.CompleteActivity("parentTask", parentVars);

        // Act — check child's variables
        var parentId = parentInstance.GetPrimaryKey();
        var parentSnapshot = await QueryService.GetStateSnapshot(parentId);
        var callEntry = parentSnapshot!.ActiveActivities.FirstOrDefault(a => a.ActivityId == "call1");
        Assert.IsNotNull(callEntry);

        var childInstanceId = callEntry.ChildWorkflowInstanceId!.Value;
        var childInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(childInstanceId);
        var childSnapshot = await QueryService.GetStateSnapshot(childInstanceId);
        Assert.IsNotNull(childSnapshot);

        // Assert — child should have mapped variables only
        var childVarState = childSnapshot.VariableStates.First();
        var childVarDict = (IDictionary<string, object?>)childVarState.Variables;
        Assert.IsTrue(childVarDict.ContainsKey("orderId"));
        Assert.IsTrue(childVarDict.ContainsKey("paymentAmount"));
        Assert.IsFalse(childVarDict.ContainsKey("amount"), "Original key should not be in child");
        Assert.IsFalse(childVarDict.ContainsKey("secret"), "Unmapped variable should not be in child");
    }

    [TestMethod]
    public async Task CallActivity_ShouldMapOutputVariables_BackToParent()
    {
        // Arrange — deploy child process
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");
        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess3",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cf1", childStart, childTask),
                new SequenceFlow("cf2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Parent with output mappings: txId→transactionId
        var parentStart = new StartEvent("parentStart");
        var callActivity = new CallActivity("call1", "childProcess3",
            [],
            [new VariableMapping("txId", "transactionId")]);
        var parentTask = new TaskActivity("parentTask");
        var parentEnd = new EndEvent("parentEnd");
        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess3",
            Activities = [parentStart, callActivity, parentTask, parentEnd],
            SequenceFlows =
            [
                new SequenceFlow("pf1", parentStart, callActivity),
                new SequenceFlow("pf2", callActivity, parentTask),
                new SequenceFlow("pf3", parentTask, parentEnd)
            ]
        };

        var parentInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await parentInstance.SetWorkflow(parentWorkflow);
        await parentInstance.StartWorkflow();

        // Complete child task with variables
        var parentId = parentInstance.GetPrimaryKey();
        var parentSnapshot = await QueryService.GetStateSnapshot(parentId);
        var callEntry = parentSnapshot!.ActiveActivities.FirstOrDefault(a => a.ActivityId == "call1");
        var childInstanceId = callEntry!.ChildWorkflowInstanceId!.Value;
        var childInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(childInstanceId);

        dynamic childVars = new ExpandoObject();
        childVars.txId = "TX-123";
        childVars.internalState = "should-not-return";
        await childInstance.CompleteActivity("childTask", childVars);

        // Allow event propagation
        await Task.Delay(500);

        // Assert — parent should have mapped output variable, parentTask should be active
        var finalSnapshot = await QueryService.GetStateSnapshot(parentId);
        Assert.IsNotNull(finalSnapshot);

        var parentVarDict = (IDictionary<string, object?>)finalSnapshot.VariableStates.First().Variables;
        Assert.IsTrue(parentVarDict.ContainsKey("transactionId"), "Mapped output should be in parent");
        Assert.AreEqual("TX-123", parentVarDict["transactionId"]?.ToString());
        Assert.IsFalse(parentVarDict.ContainsKey("internalState"), "Unmapped child variable should not be in parent");
    }

    [TestMethod]
    public async Task CallActivity_BoundaryErrorEvent_ShouldRouteToRecoveryPath()
    {
        // Arrange — deploy child process
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");
        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess4",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cf1", childStart, childTask),
                new SequenceFlow("cf2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Parent with call activity + catch-all boundary error event → recovery path
        var parentStart = new StartEvent("parentStart");
        var callActivity = new CallActivity("call1", "childProcess4", [], []);
        var parentEnd = new EndEvent("parentEnd");
        var boundaryEvent = new BoundaryErrorEvent("err1", "call1", null); // catch-all
        var recoveryTask = new TaskActivity("recovery");
        var recoveryEnd = new EndEvent("recoveryEnd");

        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess4",
            Activities = [parentStart, callActivity, parentEnd, boundaryEvent, recoveryTask, recoveryEnd],
            SequenceFlows =
            [
                new SequenceFlow("pf1", parentStart, callActivity),
                new SequenceFlow("pf2", callActivity, parentEnd),
                new SequenceFlow("pf3", boundaryEvent, recoveryTask),
                new SequenceFlow("pf4", recoveryTask, recoveryEnd)
            ]
        };

        var parentInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await parentInstance.SetWorkflow(parentWorkflow);
        await parentInstance.StartWorkflow();

        // Act — fail the call activity (simulating child failure notification)
        await parentInstance.FailActivity("call1", new Exception("child failed"));

        // Assert — boundary event should route to recovery path
        var parentId = parentInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(parentId);
        Assert.IsNotNull(snapshot);

        // Recovery task should be active
        var recoveryActive = snapshot.ActiveActivities.FirstOrDefault(a => a.ActivityId == "recovery");
        Assert.IsNotNull(recoveryActive, "Recovery task should be active after boundary event");

        // Boundary event should be completed
        var boundaryCompleted = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "err1");
        Assert.IsNotNull(boundaryCompleted, "Boundary error event should be completed");
    }

    [TestMethod]
    public async Task CallActivity_WithNoMappings_ShouldIsolateVariables()
    {
        // Arrange — deploy child process
        var childStart = new StartEvent("childStart");
        var childTask = new TaskActivity("childTask");
        var childEnd = new EndEvent("childEnd");
        var childWorkflow = new WorkflowDefinition
        {
            WorkflowId = "childProcess5",
            Activities = [childStart, childTask, childEnd],
            SequenceFlows =
            [
                new SequenceFlow("cf1", childStart, childTask),
                new SequenceFlow("cf2", childTask, childEnd)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(childWorkflow, "<xml/>");

        // Parent with no mappings AND propagation disabled
        var parentStart = new StartEvent("parentStart");
        var parentTask = new TaskActivity("parentTask");
        var callActivity = new CallActivity("call1", "childProcess5", [], [],
            propagateAllParentVariables: false, propagateAllChildVariables: false);
        var parentEnd = new EndEvent("parentEnd");
        var parentWorkflow = new WorkflowDefinition
        {
            WorkflowId = "parentProcess5",
            Activities = [parentStart, parentTask, callActivity, parentEnd],
            SequenceFlows =
            [
                new SequenceFlow("pf1", parentStart, parentTask),
                new SequenceFlow("pf2", parentTask, callActivity),
                new SequenceFlow("pf3", callActivity, parentEnd)
            ]
        };

        var parentInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await parentInstance.SetWorkflow(parentWorkflow);
        await parentInstance.StartWorkflow();

        // Complete parent task with variables
        dynamic parentVars = new ExpandoObject();
        parentVars.secret = "parent-only";
        await parentInstance.CompleteActivity("parentTask", parentVars);

        // Check child has no variables from parent
        var parentId = parentInstance.GetPrimaryKey();
        var parentSnapshot = await QueryService.GetStateSnapshot(parentId);
        var callEntry = parentSnapshot!.ActiveActivities.FirstOrDefault(a => a.ActivityId == "call1");
        var childInstanceId = callEntry!.ChildWorkflowInstanceId!.Value;
        var childSnapshot = await QueryService.GetStateSnapshot(childInstanceId);
        var childVarDict = (IDictionary<string, object?>)childSnapshot!.VariableStates.First().Variables;
        Assert.IsFalse(childVarDict.ContainsKey("secret"), "Parent variable should not leak to child without mapping");

        // Complete child with variables
        dynamic childVars = new ExpandoObject();
        childVars.childSecret = "child-only";
        var childInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(childInstanceId);
        await childInstance.CompleteActivity("childTask", childVars);

        // Allow event propagation
        await Task.Delay(500);

        // Check parent doesn't have child variables
        var finalSnapshot = await QueryService.GetStateSnapshot(parentId);
        var parentVarDict = (IDictionary<string, object?>)finalSnapshot!.VariableStates.First().Variables;
        Assert.IsFalse(parentVarDict.ContainsKey("childSecret"), "Child variable should not leak to parent without mapping");
        Assert.IsTrue(finalSnapshot.IsCompleted, "Parent should be completed");
    }
}
```

**Step 2: Check that ActivitySnapshot/QueryService exposes ChildWorkflowInstanceId**

The `QueryService.GetStateSnapshot()` returns snapshots that include `ActiveActivities` and `CompletedActivities`. Check if the snapshot model includes `ChildWorkflowInstanceId`. If not, add it to the query model. Check `src/Fleans/Fleans.Application/QueryModels/` for the snapshot type and update it.

**Step 3: Run integration tests**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "CallActivity"`
Expected: All tests PASS.

**Step 4: Run full test suite**

Run: `dotnet test src/Fleans/`
Expected: All tests PASS — existing tests should not be broken.

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/CallActivityTests.cs
git commit -m "test: add Call Activity integration tests for completion, variable mapping, and boundary events"
```

---

### Task 9: Final Verification & README

**Files:**
- Modify: `README.md` (BPMN coverage table)

**Step 1: Run full test suite**

Run: `dotnet test src/Fleans/`
Expected: All tests PASS.

**Step 2: Run the full stack via Aspire**

Run: `dotnet build src/Fleans/Fleans.Aspire/`
Expected: PASS (verifies all projects compile together).

**Step 3: Update BPMN coverage table in README.md**

Add to the BPMN elements table:

| Call Activity | `callActivity` | Invokes external process definition by key |
| Boundary Error Event | `boundaryEvent` with `errorEventDefinition` | Catches errors on attached activity, routes to recovery |

**Step 4: Commit**

```bash
git add README.md
git commit -m "docs: add Call Activity and Boundary Error Event to BPMN coverage table"
```
