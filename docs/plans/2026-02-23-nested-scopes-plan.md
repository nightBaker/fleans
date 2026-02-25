# Nested Scopes — Embedded Sub-Process Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement BPMN `<subProcess>` with variable scope chains — activities nested inside a sub-process share parent variables via walk-up, scoped cancellation on boundary events, and recursive BPMN parsing.

**Architecture:** SubProcess executes within the same WorkflowInstance grain (no child grain spawning). It implements `IWorkflowDefinition` so child activities use it as their definition scope. Variable scopes form a chain via `ParentVariablesId` with walk-up reads and local-scope writes. `ScopeId` on `ActivityInstanceEntry` tracks which sub-process spawned each activity for scoped cancellation.

**Tech Stack:** Orleans grains, `[GenerateSerializer]`, MSTest + Orleans.TestingHost, NSubstitute

---

## Task 1: Domain — ParentVariablesId on WorkflowVariablesState

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowVariablesState.cs:18-25`

**Step 1: Add ParentVariablesId property**

Add after the `WorkflowInstanceId` property (line 22), before the `Variables` property (line 24):

```csharp
[Id(3)]
public Guid? ParentVariablesId { get; private set; }
```

**Step 2: Add constructor overload for child scopes**

Add after the existing private constructor (line 14-16):

```csharp
public WorkflowVariablesState(Guid id, Guid workflowInstanceId, Guid parentVariablesId)
{
    Id = id;
    WorkflowInstanceId = workflowInstanceId;
    ParentVariablesId = parentVariablesId;
}
```

**Step 3: Build to verify no regressions**

Run: `dotnet build src/Fleans/`
Expected: BUILD SUCCEEDED.

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/WorkflowVariablesState.cs
git commit -m "feat: add ParentVariablesId to WorkflowVariablesState for scope chain"
```

---

## Task 2: Domain — ScopeId on ActivityInstanceEntry

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs:6-11,17-30`

**Step 1: Add ScopeId property**

Add after the `ChildWorkflowInstanceId` property (line 30):

```csharp
[Id(5)]
public Guid? ScopeId { get; private set; }
```

**Step 2: Add constructor overload with ScopeId**

Add after the existing private constructor (line 13-15):

```csharp
public ActivityInstanceEntry(Guid activityInstanceId, string activityId, Guid workflowInstanceId, Guid scopeId)
    : this(activityInstanceId, activityId, workflowInstanceId)
{
    ScopeId = scopeId;
}
```

**Step 3: Build to verify no regressions**

Run: `dotnet build src/Fleans/`
Expected: BUILD SUCCEEDED.

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs
git commit -m "feat: add ScopeId to ActivityInstanceEntry for scope tracking"
```

---

## Task 3: Domain — AddChildVariableState on WorkflowInstanceState

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs:96-104`

**Step 1: Add AddChildVariableState method**

Add after the `AddCloneOfVariableState` method (after line 104):

```csharp
public Guid AddChildVariableState(Guid parentVariablesId)
{
    var childState = new WorkflowVariablesState(Guid.NewGuid(), Id, parentVariablesId);
    VariableStates.Add(childState);
    return childState.Id;
}
```

**Step 2: Build to verify no regressions**

Run: `dotnet build src/Fleans/`
Expected: BUILD SUCCEEDED.

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs
git commit -m "feat: add AddChildVariableState for sub-process scope creation"
```

---

## Task 4: Domain — SubProcess activity class + domain tests

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/SubProcess.cs`
- Create: `src/Fleans/Fleans.Domain.Tests/SubProcessActivityTests.cs`

**Step 1: Write failing domain tests**

Create `src/Fleans/Fleans.Domain.Tests/SubProcessActivityTests.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class SubProcessActivityTests
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldCallExecuteButNotComplete()
    {
        // Arrange — SubProcess should NOT call Complete (it waits for children)
        var innerStart = new StartEvent("sub_start");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerEnd],
            SequenceFlows = [new SequenceFlow("sf1", innerStart, innerEnd)]
        };

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [subProcess],
            []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sub1");

        // Act
        await subProcess.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — Execute called, Complete NOT called
        await activityContext.Received(1).Execute();
        await activityContext.DidNotReceive().Complete();
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnOutgoingFlowsFromParentDefinition()
    {
        // Arrange
        var innerStart = new StartEvent("sub_start");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerEnd],
            SequenceFlows = [new SequenceFlow("inner_f1", innerStart, innerEnd)]
        };
        var endEvent = new EndEvent("end");

        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [subProcess, endEvent],
            [new SequenceFlow("f1", subProcess, endEvent)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sub1");

        // Act
        var nextActivities = await subProcess.GetNextActivities(workflowContext, activityContext, definition);

        // Assert — should return parent-level flows, not internal flows
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public void SubProcess_ShouldImplementIWorkflowDefinition()
    {
        // Arrange
        var innerStart = new StartEvent("sub_start");
        var innerTask = new TaskActivity("sub_task");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", innerStart, innerTask),
                new SequenceFlow("sf2", innerTask, innerEnd)
            ]
        };

        // Act & Assert — IWorkflowDefinition members
        IWorkflowDefinition def = subProcess;
        Assert.AreEqual("sub1", def.WorkflowId);
        Assert.HasCount(3, def.Activities);
        Assert.HasCount(2, def.SequenceFlows);
        Assert.AreEqual("sub_task", def.GetActivity("sub_task").ActivityId);
    }
}
```

**Step 2: Run tests — verify they fail**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~SubProcessActivity" --verbosity minimal`
Expected: FAIL — `SubProcess` class doesn't exist.

**Step 3: Write the SubProcess activity class**

Create `src/Fleans/Fleans.Domain/Activities/SubProcess.cs`:

```csharp
using Fleans.Domain.Sequences;
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record SubProcess(string ActivityId) : BoundarableActivity(ActivityId), IWorkflowDefinition
{
    [Id(1)]
    public List<Activity> Activities { get; init; } = [];

    [Id(2)]
    public List<SequenceFlow> SequenceFlows { get; init; } = [];

    // IWorkflowDefinition — SubProcess acts as the definition scope for its children
    public string WorkflowId => ActivityId;
    public string? ProcessDefinitionId => null;
    public List<MessageDefinition> Messages { get; init; } = [];
    public List<SignalDefinition> Signals { get; init; } = [];

    public Activity GetActivity(string activityId)
        => Activities.First(a => a.ActivityId == activityId);

    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        // Publish executed event but do NOT call Complete — sub-process waits for children.
        // The WorkflowInstance grain handles initializing child entries.
        await activityContext.Execute();
        await activityContext.PublishEvent(new Events.WorkflowActivityExecutedEvent(
            await workflowContext.GetWorkflowInstanceId(),
            definition.WorkflowId,
            await activityContext.GetActivityInstanceId(),
            ActivityId,
            GetType().Name));
    }

    internal override Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        // Return outgoing flows from the PARENT definition (the one containing this SubProcess)
        var nextFlows = definition.SequenceFlows
            .Where(sf => sf.Source == this)
            .Select(flow => flow.Target)
            .ToList();
        return Task.FromResult(nextFlows);
    }
}
```

**Step 4: Run tests — verify they pass**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~SubProcessActivity" --verbosity minimal`
Expected: 3 tests PASS.

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/SubProcess.cs src/Fleans/Fleans.Domain.Tests/SubProcessActivityTests.cs
git commit -m "feat: add SubProcess domain activity class with tests"
```

---

## Task 5: Domain — EndEvent scope awareness

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/EndEvent.cs:11-17`
- Create: `src/Fleans/Fleans.Domain.Tests/EndEventScopeTests.cs`

**Step 1: Write failing test**

Create `src/Fleans/Fleans.Domain.Tests/EndEventScopeTests.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class EndEventScopeTests
{
    [TestMethod]
    public async Task ExecuteAsync_InsideSubProcess_ShouldNotCompleteWorkflow()
    {
        // Arrange — EndEvent inside a SubProcess should NOT call workflowContext.Complete()
        var innerStart = new StartEvent("sub_start");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerEnd],
            SequenceFlows = [new SequenceFlow("sf1", innerStart, innerEnd)]
        };

        // The definition passed to ExecuteAsync is the SubProcess (which implements IWorkflowDefinition)
        IWorkflowDefinition scopeDefinition = subProcess;
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(scopeDefinition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("sub_end");

        // Act
        await innerEnd.ExecuteAsync(workflowContext, activityContext, scopeDefinition);

        // Assert — activity completed, but workflow NOT completed
        await activityContext.Received(1).Complete();
        await workflowContext.DidNotReceive().Complete();
    }

    [TestMethod]
    public async Task ExecuteAsync_AtRootLevel_ShouldCompleteWorkflow()
    {
        // Arrange — EndEvent at root level should still call workflowContext.Complete()
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var definition = ActivityTestHelper.CreateWorkflowDefinition(
            [start, end],
            [new SequenceFlow("f1", start, end)]);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, _) = ActivityTestHelper.CreateActivityContext("end");

        // Act
        await end.ExecuteAsync(workflowContext, activityContext, definition);

        // Assert — both activity and workflow completed
        await activityContext.Received(1).Complete();
        await workflowContext.Received(1).Complete();
    }
}
```

**Step 2: Run tests — verify the SubProcess test fails**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~EndEventScope" --verbosity minimal`
Expected: `ExecuteAsync_InsideSubProcess_ShouldNotCompleteWorkflow` FAILS — EndEvent currently always calls `workflowContext.Complete()`.

**Step 3: Modify EndEvent to be scope-aware**

In `src/Fleans/Fleans.Domain/Activities/EndEvent.cs`, change `ExecuteAsync`:

```csharp
internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);

    await activityContext.Complete();

    // Only complete the workflow if this is a top-level EndEvent (not inside a sub-process)
    if (definition is not SubProcess)
        await workflowContext.Complete();
}
```

**Step 4: Run tests — verify they pass**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~EndEventScope" --verbosity minimal`
Expected: 2 tests PASS.

**Step 5: Run ALL domain tests to verify no regression**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --verbosity minimal`
Expected: All tests pass.

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/EndEvent.cs src/Fleans/Fleans.Domain.Tests/EndEventScopeTests.cs
git commit -m "feat: make EndEvent scope-aware — skip workflow Complete inside SubProcess"
```

---

## Task 6: Domain — Variable scope walk-up tests

**Files:**
- Create: `src/Fleans/Fleans.Domain.Tests/VariableScopeChainTests.cs`

**Step 1: Write variable scope chain tests**

Create `src/Fleans/Fleans.Domain.Tests/VariableScopeChainTests.cs`:

```csharp
using Fleans.Domain.States;
using System.Dynamic;

namespace Fleans.Domain.Tests;

[TestClass]
public class VariableScopeChainTests
{
    [TestMethod]
    public void AddChildVariableState_ShouldSetParentVariablesId()
    {
        // Arrange
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        // Act
        var childVarsId = state.AddChildVariableState(rootVarsId);

        // Assert
        var childState = state.GetVariableState(childVarsId);
        Assert.IsNotNull(childState);
        Assert.AreEqual(rootVarsId, childState.ParentVariablesId);
    }

    [TestMethod]
    public void ChildScope_ShouldStartEmpty()
    {
        // Arrange
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        // Set parent variable
        dynamic parentVars = new ExpandoObject();
        parentVars.color = "red";
        state.MergeState(rootVarsId, parentVars);

        // Act
        var childVarsId = state.AddChildVariableState(rootVarsId);

        // Assert — child scope starts empty (variables inherited via walk-up, not copied)
        var childState = state.GetVariableState(childVarsId);
        var childDict = (IDictionary<string, object?>)childState.Variables;
        Assert.HasCount(0, childDict);
    }

    [TestMethod]
    public void RootScope_ShouldHaveNullParentVariablesId()
    {
        // Arrange
        var state = new WorkflowInstanceState();
        var rootVarsId = Guid.NewGuid();
        var entry = new ActivityInstanceEntry(Guid.NewGuid(), "start", Guid.NewGuid());
        state.StartWith(Guid.NewGuid(), null, entry, rootVarsId);

        // Assert
        var rootState = state.GetVariableState(rootVarsId);
        Assert.IsNull(rootState.ParentVariablesId);
    }
}
```

**Step 2: Run tests — verify they pass**

Run: `dotnet test src/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~VariableScopeChain" --verbosity minimal`
Expected: 3 tests PASS.

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Domain.Tests/VariableScopeChainTests.cs
git commit -m "test: add variable scope chain domain tests"
```

---

## Task 7: Infrastructure — BpmnConverter recursive parsing + tests

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs:66-340` (ParseActivities) and `342-380` (ParseSequenceFlows)
- Create: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/SubProcessTests.cs`
- Modify: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/BpmnConverterTestBase.cs` (add helpers)

**Step 1: Write failing tests**

Create `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/SubProcessTests.cs`:

```csharp
using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class SubProcessTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSubProcess()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithSubProcess("sp-workflow", "sub1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert — SubProcess is in root activities
        var subProcess = workflow.Activities.OfType<SubProcess>().FirstOrDefault(s => s.ActivityId == "sub1");
        Assert.IsNotNull(subProcess, "SubProcess should be parsed");
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_SubProcess_ShouldContainChildActivities()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithSubProcess("sp-workflow", "sub1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert — child activities are inside SubProcess, not in root
        var subProcess = workflow.Activities.OfType<SubProcess>().First(s => s.ActivityId == "sub1");
        Assert.IsTrue(subProcess.Activities.Any(a => a.ActivityId == "sub1_start"), "SubProcess should contain its StartEvent");
        Assert.IsTrue(subProcess.Activities.Any(a => a.ActivityId == "sub1_task"), "SubProcess should contain its Task");
        Assert.IsTrue(subProcess.Activities.Any(a => a.ActivityId == "sub1_end"), "SubProcess should contain its EndEvent");

        // Root should NOT contain sub-process children
        Assert.IsFalse(workflow.Activities.Any(a => a.ActivityId == "sub1_start"),
            "Root should not contain sub-process children");
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_SubProcess_ShouldContainInternalFlows()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithSubProcess("sp-workflow", "sub1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert — internal flows are on SubProcess, not root
        var subProcess = workflow.Activities.OfType<SubProcess>().First(s => s.ActivityId == "sub1");
        Assert.HasCount(2, subProcess.SequenceFlows);

        // Root flows should only be parent-level
        Assert.IsTrue(workflow.SequenceFlows.All(sf =>
            sf.Source.ActivityId != "sub1_start" && sf.Source.ActivityId != "sub1_task"),
            "Root flows should not contain sub-process internal flows");
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_NestedSubProcess_ShouldParseRecursively()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithNestedSubProcess("nested-workflow");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var outer = workflow.Activities.OfType<SubProcess>().First(s => s.ActivityId == "outer");
        Assert.IsNotNull(outer);

        var inner = outer.Activities.OfType<SubProcess>().FirstOrDefault(s => s.ActivityId == "inner");
        Assert.IsNotNull(inner, "Nested SubProcess should be parsed inside outer");
        Assert.IsTrue(inner.Activities.Any(a => a.ActivityId == "inner_task"), "Inner SubProcess should contain its task");
    }
}
```

**Step 2: Add XML helpers to BpmnConverterTestBase**

Add to the end of `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/BpmnConverterTestBase.cs` (before the closing `}`):

```csharp
protected static string CreateBpmnWithSubProcess(string processId, string subProcessId)
{
    return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <subProcess id=""{subProcessId}"">
      <startEvent id=""{subProcessId}_start"" />
      <task id=""{subProcessId}_task"" />
      <endEvent id=""{subProcessId}_end"" />
      <sequenceFlow id=""inner_f1"" sourceRef=""{subProcessId}_start"" targetRef=""{subProcessId}_task"" />
      <sequenceFlow id=""inner_f2"" sourceRef=""{subProcessId}_task"" targetRef=""{subProcessId}_end"" />
    </subProcess>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""{subProcessId}"" />
    <sequenceFlow id=""f2"" sourceRef=""{subProcessId}"" targetRef=""end"" />
  </process>
</definitions>";
}

protected static string CreateBpmnWithNestedSubProcess(string processId)
{
    return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <subProcess id=""outer"">
      <startEvent id=""outer_start"" />
      <subProcess id=""inner"">
        <startEvent id=""inner_start"" />
        <task id=""inner_task"" />
        <endEvent id=""inner_end"" />
        <sequenceFlow id=""inner_f1"" sourceRef=""inner_start"" targetRef=""inner_task"" />
        <sequenceFlow id=""inner_f2"" sourceRef=""inner_task"" targetRef=""inner_end"" />
      </subProcess>
      <endEvent id=""outer_end"" />
      <sequenceFlow id=""outer_f1"" sourceRef=""outer_start"" targetRef=""inner"" />
      <sequenceFlow id=""outer_f2"" sourceRef=""inner"" targetRef=""outer_end"" />
    </subProcess>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""outer"" />
    <sequenceFlow id=""f2"" sourceRef=""outer"" targetRef=""end"" />
  </process>
</definitions>";
}
```

**Step 3: Run tests — verify they fail**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/ --filter "FullyQualifiedName~SubProcessTests" --verbosity minimal`
Expected: FAIL — SubProcess parsing not implemented.

**Step 4: Refactor BpmnConverter for recursive parsing**

In `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs`:

**4a.** Change `ParseActivities` signature to accept separate output lists and return child scope data:

Replace the method signature at line 66:
```csharp
private void ParseActivities(XElement process, List<Activity> activities, Dictionary<string, Activity> activityMap, HashSet<string> defaultFlowIds)
```
with:
```csharp
private void ParseActivities(XElement scopeElement, List<Activity> activities, Dictionary<string, Activity> activityMap, HashSet<string> defaultFlowIds)
```

**4b.** Change ALL `.Descendants(...)` calls inside ParseActivities to `.Elements(...)`:

Replace every occurrence of `process.Descendants(Bpmn + ` with `scopeElement.Elements(Bpmn + ` inside the `ParseActivities` method (lines 69-339). This is critical — `.Descendants()` flattens sub-process children into the parent; `.Elements()` only returns direct children.

Similarly, change all `process.Descendants` to `scopeElement.Elements` in `ParseSequenceFlows` (line 342-380).

**4c.** Add sub-process parsing block after the event-based gateway parsing (after line 261, before `// Parse call activities`):

```csharp
// Parse sub-processes (recursive)
foreach (var subProcessEl in scopeElement.Elements(Bpmn + "subProcess"))
{
    var id = GetId(subProcessEl);
    var childActivities = new List<Activity>();
    var childDefaultFlowIds = new HashSet<string>();

    // Recursively parse child activities
    ParseActivities(subProcessEl, childActivities, activityMap, childDefaultFlowIds);

    // Parse child sequence flows
    var childSequenceFlows = new List<SequenceFlow>();
    ParseSequenceFlows(subProcessEl, childSequenceFlows, activityMap, childDefaultFlowIds);

    var activity = new SubProcess(id)
    {
        Activities = childActivities,
        SequenceFlows = childSequenceFlows
    };
    activities.Add(activity);
    activityMap[id] = activity;
}
```

**4d.** Similarly update `ParseSequenceFlows` — change `process.Descendants` to `scopeElement.Elements`:

Replace the method signature at line 342:
```csharp
private void ParseSequenceFlows(XElement process, List<SequenceFlow> sequenceFlows, Dictionary<string, Activity> activityMap, HashSet<string> defaultFlowIds)
```
with:
```csharp
private void ParseSequenceFlows(XElement scopeElement, List<SequenceFlow> sequenceFlows, Dictionary<string, Activity> activityMap, HashSet<string> defaultFlowIds)
```

And change `process.Descendants(Bpmn + "sequenceFlow")` at line 344 to `scopeElement.Elements(Bpmn + "sequenceFlow")`.

**Step 5: Run sub-process tests — verify they pass**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/ --filter "FullyQualifiedName~SubProcessTests" --verbosity minimal`
Expected: 4 tests PASS.

**Step 6: Run ALL infrastructure tests to verify no regression**

Run: `dotnet test src/Fleans/Fleans.Infrastructure.Tests/ --verbosity minimal`
Expected: All tests pass. If any existing tests fail because `.Elements()` doesn't find elements that are no longer direct children (this shouldn't happen since existing BPMN has no sub-processes), investigate and fix.

**Step 7: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/SubProcessTests.cs src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/BpmnConverterTestBase.cs
git commit -m "feat: parse <subProcess> recursively in BpmnConverter"
```

---

## Task 8: Application — GetDefinitionForActivity helper

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs:59-81` (ExecuteWorkflow), add new method

**Step 1: Add GetDefinitionForActivity method**

Add after `GetWorkflowDefinition()` (after line 473):

```csharp
private static IWorkflowDefinition GetDefinitionForActivity(string activityId, IWorkflowDefinition rootDefinition)
{
    if (rootDefinition.Activities.Any(a => a.ActivityId == activityId))
        return rootDefinition;

    foreach (var subProcess in rootDefinition.Activities.OfType<SubProcess>())
    {
        var result = GetDefinitionForActivity(activityId, subProcess);
        if (result is not null)
            return result;
    }

    throw new InvalidOperationException($"Activity '{activityId}' not found in any definition scope");
}
```

**Step 2: Add FindActivity helper on IWorkflowDefinition for flat lookups across scopes**

Add to `src/Fleans/Fleans.Domain/Definitions/Workflow.cs`, a static helper method on `WorkflowDefinition` (or as an extension). Actually, it's simpler to just use `GetDefinitionForActivity` in the grain and call `.GetActivity()` on the result. No new interface method needed.

**Step 3: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: BUILD SUCCEEDED.

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "feat: add GetDefinitionForActivity for scope-aware activity resolution"
```

---

## Task 9: Application — Scope-aware execution loop + SubProcess initialization + scope completion

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs:59-207` (ExecuteWorkflow, TransitionToNextActivity)
- Create: `src/Fleans/Fleans.Application.Tests/SubProcessTests.cs`

**Step 1: Write the first integration test (simple sub-process flow)**

Create `src/Fleans/Fleans.Application.Tests/SubProcessTests.cs`:

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class SubProcessTests : WorkflowTestBase
{
    [TestMethod]
    public async Task SubProcess_SimpleFlow_ShouldCompleteThrough()
    {
        // Arrange — Start → SubProcess(Start → Task → End) → End
        var start = new StartEvent("start");
        var innerStart = new StartEvent("sub_start");
        var innerTask = new TaskActivity("sub_task");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("inner_f1", innerStart, innerTask),
                new SequenceFlow("inner_f2", innerTask, innerEnd)
            ]
        };
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sub-process-simple",
            Activities = [start, subProcess, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, subProcess),
                new SequenceFlow("f2", subProcess, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — complete the sub-process task
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);

        // Workflow should be suspended at sub_task
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should be suspended at sub-process task");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "sub_task"),
            "sub_task should be active");

        await workflowInstance.CompleteActivity("sub_task", new ExpandoObject());

        // Assert — workflow should be fully completed
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "sub_start"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "sub_task"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "sub_end"));
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"));
    }

    [TestMethod]
    public async Task SubProcess_VariableInheritance_ShouldReadParentVariable()
    {
        // Arrange — Start → Task(sets color=red) → SubProcess(Start → ScriptTask(reads color) → End) → End
        var start = new StartEvent("start");
        var parentTask = new TaskActivity("parentTask");
        var innerStart = new StartEvent("sub_start");
        var innerScript = new ScriptTask("sub_script", "color + \"-modified\"", "result");
        var innerEnd = new EndEvent("sub_end");
        var subProcess = new SubProcess("sub1")
        {
            Activities = [innerStart, innerScript, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("inner_f1", innerStart, innerScript),
                new SequenceFlow("inner_f2", innerScript, innerEnd)
            ]
        };
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sub-process-var-inherit",
            Activities = [start, parentTask, subProcess, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, parentTask),
                new SequenceFlow("f2", parentTask, subProcess),
                new SequenceFlow("f3", subProcess, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete parent task with variable
        dynamic vars = new ExpandoObject();
        vars.color = "red";
        await workflowInstance.CompleteActivity("parentTask", vars);

        // Assert — workflow should complete (script reads parent variable via walk-up)
        var instanceId = workflowInstance.GetPrimaryKey();
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should complete — script should read parent var via walk-up");
    }

    [TestMethod]
    public async Task SubProcess_NestedSubProcess_ShouldCompleteThrough()
    {
        // Arrange — Start → Outer(Start → Inner(Start → Task → End) → End) → End
        var start = new StartEvent("start");

        var innerStart = new StartEvent("inner_start");
        var innerTask = new TaskActivity("inner_task");
        var innerEnd = new EndEvent("inner_end");
        var innerSub = new SubProcess("inner")
        {
            Activities = [innerStart, innerTask, innerEnd],
            SequenceFlows =
            [
                new SequenceFlow("inner_f1", innerStart, innerTask),
                new SequenceFlow("inner_f2", innerTask, innerEnd)
            ]
        };

        var outerStart = new StartEvent("outer_start");
        var outerEnd = new EndEvent("outer_end");
        var outerSub = new SubProcess("outer")
        {
            Activities = [outerStart, innerSub, outerEnd],
            SequenceFlows =
            [
                new SequenceFlow("outer_f1", outerStart, innerSub),
                new SequenceFlow("outer_f2", innerSub, outerEnd)
            ]
        };

        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "nested-sub-process",
            Activities = [start, outerSub, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, outerSub),
                new SequenceFlow("f2", outerSub, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Complete the innermost task
        await workflowInstance.CompleteActivity("inner_task", new ExpandoObject());

        // Assert — entire workflow should complete
        var instanceId = workflowInstance.GetPrimaryKey();
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Nested sub-process workflow should complete");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"));
    }
}
```

**Step 2: Run tests — verify they fail**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~SubProcessTests" --verbosity minimal`
Expected: FAIL — SubProcess execution not implemented in WorkflowInstance.

**Step 3: Modify ExecuteWorkflow for scope-aware activity resolution and SubProcess initialization**

In `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`, modify `ExecuteWorkflow()` (lines 59-81):

```csharp
private async Task ExecuteWorkflow()
{
    var definition = await GetWorkflowDefinition();
    while (await AnyNotExecuting())
    {
        foreach (var activityState in await GetNotExecutingNotCompletedActivities())
        {
            var activityId = await activityState.GetActivityId();
            var scopeDefinition = GetDefinitionForActivity(activityId, definition);
            var currentActivity = scopeDefinition.GetActivity(activityId);
            SetActivityRequestContext(activityId, activityState);
            LogExecutingActivity(activityId, currentActivity.GetType().Name);
            await currentActivity.ExecuteAsync(this, activityState, scopeDefinition);

            // Initialize sub-process children
            if (currentActivity is SubProcess subProcess)
            {
                await InitializeSubProcessChildren(subProcess, activityState);
            }

            if (currentActivity is IBoundarableActivity boundarable)
            {
                await boundarable.RegisterBoundaryEventsAsync(this, activityState, scopeDefinition);
            }
        }

        await TransitionToNextActivity();
        LogStatePersistedAfterTransition();
        await _state.WriteStateAsync();
    }
}
```

**Step 4: Add InitializeSubProcessChildren method**

Add after `ExecuteWorkflow()`:

```csharp
private async Task InitializeSubProcessChildren(SubProcess subProcess, IActivityInstanceGrain subProcessInstance)
{
    var subProcessInstanceId = await subProcessInstance.GetActivityInstanceId();
    var parentVariablesId = await subProcessInstance.GetVariablesStateId();

    // Create child variable scope with parent link
    var childVariablesId = State.AddChildVariableState(parentVariablesId);
    LogSubProcessInitialized(subProcess.ActivityId, childVariablesId);

    // Find the sub-process's internal start event
    var startActivity = subProcess.Activities.FirstOrDefault(a => a is StartEvent)
        ?? throw new InvalidOperationException($"SubProcess '{subProcess.ActivityId}' must have a StartEvent");

    // Create entry for the start event, tagged with the sub-process scope
    var startInstanceId = Guid.NewGuid();
    var startInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(startInstanceId);
    await startInstance.SetActivity(startActivity.ActivityId, startActivity.GetType().Name);
    await startInstance.SetVariablesId(childVariablesId);

    var startEntry = new ActivityInstanceEntry(startInstanceId, startActivity.ActivityId, State.Id, subProcessInstanceId);
    State.AddEntries([startEntry]);
}
```

**Step 5: Modify TransitionToNextActivity for scope-aware definition resolution and scope tagging**

In `TransitionToNextActivity()` (lines 145-207), make these changes:

**5a.** Change the activity lookup to use scope-aware resolution (line 166):

Replace:
```csharp
var currentActivity = definition.GetActivity(entry.ActivityId);
```
with:
```csharp
var scopeDefinition = GetDefinitionForActivity(entry.ActivityId, definition);
var currentActivity = scopeDefinition.GetActivity(entry.ActivityId);
```

**5b.** Change GetNextActivities to use scope definition (line 168):

Replace:
```csharp
var nextActivities = await currentActivity.GetNextActivities(this, activityInstance, definition);
```
with:
```csharp
var nextActivities = await currentActivity.GetNextActivities(this, activityInstance, scopeDefinition);
```

**5c.** Tag new entries with the same ScopeId as the completed entry (line 197):

Replace:
```csharp
newActiveEntries.Add(new ActivityInstanceEntry(newId, nextActivity.ActivityId, State.Id));
```
with:
```csharp
newActiveEntries.Add(entry.ScopeId.HasValue
    ? new ActivityInstanceEntry(newId, nextActivity.ActivityId, State.Id, entry.ScopeId.Value)
    : new ActivityInstanceEntry(newId, nextActivity.ActivityId, State.Id));
```

**Step 6: Add sub-process scope completion detection**

Add after TransitionToNextActivity's `State.AddEntries(newActiveEntries)` (after line 206), but still inside the method:

```csharp
// Check for completed sub-process scopes
await CompleteFinishedSubProcessScopes(definition);
```

And add the new method:

```csharp
private async Task CompleteFinishedSubProcessScopes(IWorkflowDefinition definition)
{
    bool anyCompleted;
    do
    {
        anyCompleted = false;
        foreach (var entry in State.GetActiveActivities().ToList())
        {
            var scopeDefinition = GetDefinitionForActivity(entry.ActivityId, definition);
            var activity = scopeDefinition.GetActivity(entry.ActivityId);
            if (activity is not SubProcess) continue;

            // Check if all entries in this scope are completed
            var scopeEntries = State.Entries.Where(e => e.ScopeId == entry.ActivityInstanceId).ToList();
            if (scopeEntries.Count == 0) continue; // Not initialized yet
            if (!scopeEntries.All(e => e.IsCompleted)) continue;

            // All scope children are done — complete the sub-process itself
            var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
            await activityInstance.Complete();
            LogSubProcessCompleted(entry.ActivityId);

            // Transition from sub-process to parent's next activities
            var parentDefinition = GetParentDefinition(entry.ActivityId, definition);
            var nextActivities = await activity.GetNextActivities(this, activityInstance, parentDefinition);

            var sourceVariablesId = await activityInstance.GetVariablesStateId();
            var completedEntries = new List<ActivityInstanceEntry> { entry };
            var newEntries = new List<ActivityInstanceEntry>();

            foreach (var nextActivity in nextActivities)
            {
                var newId = Guid.NewGuid();
                var newInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(newId);
                await newInstance.SetVariablesId(sourceVariablesId);
                await newInstance.SetActivity(nextActivity.ActivityId, nextActivity.GetType().Name);

                newEntries.Add(entry.ScopeId.HasValue
                    ? new ActivityInstanceEntry(newId, nextActivity.ActivityId, State.Id, entry.ScopeId.Value)
                    : new ActivityInstanceEntry(newId, nextActivity.ActivityId, State.Id));
            }

            State.CompleteEntries(completedEntries);
            State.AddEntries(newEntries);
            anyCompleted = true;
        }
    } while (anyCompleted); // Loop for nested sub-process completion
}
```

**Step 7: Add GetParentDefinition helper**

```csharp
private static IWorkflowDefinition GetParentDefinition(string activityId, IWorkflowDefinition rootDefinition)
{
    // For root-level activities, the parent is the root definition itself
    if (rootDefinition.Activities.Any(a => a.ActivityId == activityId))
        return rootDefinition;

    foreach (var subProcess in rootDefinition.Activities.OfType<SubProcess>())
    {
        if (subProcess.Activities.Any(a => a.ActivityId == activityId))
            return rootDefinition; // Parent of activities inside this sub-process is the root

        var result = GetParentDefinitionRecursive(activityId, subProcess, rootDefinition);
        if (result is not null)
            return result;
    }

    throw new InvalidOperationException($"Parent definition not found for activity '{activityId}'");
}

private static IWorkflowDefinition? GetParentDefinitionRecursive(string activityId, SubProcess current, IWorkflowDefinition parent)
{
    foreach (var subProcess in current.Activities.OfType<SubProcess>())
    {
        if (subProcess.Activities.Any(a => a.ActivityId == activityId))
            return current;

        var result = GetParentDefinitionRecursive(activityId, subProcess, current);
        if (result is not null)
            return result;
    }
    return null;
}
```

**Step 8: Modify GetVariable to walk up the scope chain**

In `GetVariable` (line 649-654), replace:

```csharp
public ValueTask<object?> GetVariable(Guid variablesId, string variableName)
{
    var variableState = State.GetVariableState(variablesId);
    var dict = (IDictionary<string, object?>)variableState.Variables;
    return ValueTask.FromResult(dict.TryGetValue(variableName, out var value) ? value : null);
}
```

with:

```csharp
public ValueTask<object?> GetVariable(Guid variablesId, string variableName)
{
    var current = State.GetVariableState(variablesId);
    while (current is not null)
    {
        var dict = (IDictionary<string, object?>)current.Variables;
        if (dict.TryGetValue(variableName, out var value))
            return ValueTask.FromResult(value);

        current = current.ParentVariablesId.HasValue
            ? State.GetVariableState(current.ParentVariablesId.Value)
            : null;
    }
    return ValueTask.FromResult<object?>(null);
}
```

**Step 9: Modify HandleTimerFired for scope-aware activity lookup**

In `HandleTimerFired` (line 111), replace:

```csharp
var activity = definition.Activities.FirstOrDefault(a => a.ActivityId == timerActivityId);
```

with:

```csharp
Activity? activity;
try { activity = GetDefinitionForActivity(timerActivityId, definition).GetActivity(timerActivityId); }
catch { activity = null; }
```

**Step 10: Modify HandleMessageDelivery for scope-aware activity lookup**

In `HandleMessageDelivery` (line 735), replace:

```csharp
var activity = definition.GetActivity(activityId);
```

with:

```csharp
var scopeDef = GetDefinitionForActivity(activityId, definition);
var activity = scopeDef.GetActivity(activityId);
```

**Step 11: Modify HandleSignalDelivery similarly**

In `HandleSignalDelivery` (line 818), replace:

```csharp
var activity = definition.GetActivity(activityId);
```

with:

```csharp
var scopeDef = GetDefinitionForActivity(activityId, definition);
var activity = scopeDef.GetActivity(activityId);
```

**Step 12: Modify FailActivityWithBoundaryCheck for scope-aware lookup**

In `FailActivityWithBoundaryCheck` (line 567), replace:

```csharp
var boundaryEvent = definition.Activities
    .OfType<BoundaryErrorEvent>()
    .FirstOrDefault(b => b.AttachedToActivityId == activityId
```

with:

```csharp
var scopeDef = GetDefinitionForActivity(activityId, definition);
var boundaryEvent = scopeDef.Activities
    .OfType<BoundaryErrorEvent>()
    .FirstOrDefault(b => b.AttachedToActivityId == activityId
```

**Step 13: Add LoggerMessage entries**

Add after the last LoggerMessage (line 960):

```csharp
[LoggerMessage(EventId = 1034, Level = LogLevel.Information,
    Message = "Sub-process {ActivityId} initialized with child variable scope {ChildVariablesId}")]
private partial void LogSubProcessInitialized(string activityId, Guid childVariablesId);

[LoggerMessage(EventId = 1035, Level = LogLevel.Information,
    Message = "Sub-process {ActivityId} completed — all child activities done")]
private partial void LogSubProcessCompleted(string activityId);
```

**Step 14: Run integration tests — verify they pass**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~SubProcessTests" --verbosity minimal`
Expected: 3 tests PASS.

**Step 15: Run ALL tests to verify no regression**

Run: `dotnet test src/Fleans/ --verbosity minimal`
Expected: All tests pass.

**Step 16: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs src/Fleans/Fleans.Application.Tests/SubProcessTests.cs
git commit -m "feat: scope-aware execution loop with SubProcess initialization and completion"
```

---

## Task 10: Application — Boundary event cancellation on SubProcess + integration tests

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs` (add scope cancellation)
- Modify: `src/Fleans/Fleans.Application.Tests/SubProcessTests.cs` (add boundary tests)

**Step 1: Write boundary timer integration test**

Add to `src/Fleans/Fleans.Application.Tests/SubProcessTests.cs`:

```csharp
[TestMethod]
public async Task SubProcess_BoundaryTimer_ShouldCancelChildActivities()
{
    // Arrange — Start → SubProcess(Start → Task → End) + BoundaryTimer → EndBoundary
    var start = new StartEvent("start");
    var innerStart = new StartEvent("sub_start");
    var innerTask = new TaskActivity("sub_task");
    var innerEnd = new EndEvent("sub_end");
    var subProcess = new SubProcess("sub1")
    {
        Activities = [innerStart, innerTask, innerEnd],
        SequenceFlows =
        [
            new SequenceFlow("inner_f1", innerStart, innerTask),
            new SequenceFlow("inner_f2", innerTask, innerEnd)
        ]
    };
    var boundaryTimer = new BoundaryTimerEvent("boundary_timer", "sub1",
        new TimerDefinition(TimerType.Duration, "PT1S"));
    var endBoundary = new EndEvent("endBoundary");
    var end = new EndEvent("end");

    var workflow = new WorkflowDefinition
    {
        WorkflowId = "sub-process-boundary-timer",
        Activities = [start, subProcess, boundaryTimer, endBoundary, end],
        SequenceFlows =
        [
            new SequenceFlow("f1", start, subProcess),
            new SequenceFlow("f2", subProcess, end),
            new SequenceFlow("f3", boundaryTimer, endBoundary)
        ]
    };

    var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
    await workflowInstance.SetWorkflow(workflow);
    await workflowInstance.StartWorkflow();

    // Wait for boundary timer to fire (1 second + buffer)
    await Task.Delay(3000);

    // Assert — workflow completed via boundary path, sub-process children cancelled
    var instanceId = workflowInstance.GetPrimaryKey();
    var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
    Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed via boundary path");
    Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endBoundary"),
        "Boundary end event should be reached");
    Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
        "Normal end should NOT be reached");

    // sub_task should be cancelled (was active when boundary fired)
    var subTask = finalSnapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "sub_task");
    Assert.IsNotNull(subTask, "sub_task should be in completed list");
    Assert.IsTrue(subTask.IsCancelled, "sub_task should be cancelled by boundary event");
}

[TestMethod]
public async Task SubProcess_BoundaryError_ShouldCancelChildActivities()
{
    // Arrange — Start → SubProcess(Start → Task → End) + BoundaryError → EndError
    var start = new StartEvent("start");
    var innerStart = new StartEvent("sub_start");
    var innerTask = new TaskActivity("sub_task");
    var innerEnd = new EndEvent("sub_end");
    var subProcess = new SubProcess("sub1")
    {
        Activities = [innerStart, innerTask, innerEnd],
        SequenceFlows =
        [
            new SequenceFlow("inner_f1", innerStart, innerTask),
            new SequenceFlow("inner_f2", innerTask, innerEnd)
        ]
    };
    var boundaryError = new BoundaryErrorEvent("boundary_error", "sub1", null);
    var endError = new EndEvent("endError");
    var end = new EndEvent("end");

    var workflow = new WorkflowDefinition
    {
        WorkflowId = "sub-process-boundary-error",
        Activities = [start, subProcess, boundaryError, endError, end],
        SequenceFlows =
        [
            new SequenceFlow("f1", start, subProcess),
            new SequenceFlow("f2", subProcess, end),
            new SequenceFlow("f3", boundaryError, endError)
        ]
    };

    var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
    await workflowInstance.SetWorkflow(workflow);
    await workflowInstance.StartWorkflow();

    // Fail the sub-process task — should trigger boundary error on the sub-process
    await workflowInstance.FailActivity("sub_task", new Exception("Something went wrong"));

    // Assert — workflow completed via boundary error path
    var instanceId = workflowInstance.GetPrimaryKey();
    var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
    Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed via error boundary path");
    Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "endError"),
        "Error boundary end event should be reached");
    Assert.IsFalse(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
        "Normal end should NOT be reached");
}
```

**Step 2: Run tests — verify they fail**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~SubProcess_Boundary" --verbosity minimal`
Expected: FAIL — boundary events don't yet cancel scope children.

**Step 3: Add scope cancellation to BoundaryEventHandler**

When a boundary event fires on a SubProcess, the existing `HandleBoundaryTimerFiredAsync` / `HandleBoundaryErrorAsync` already cancels the host activity. We need to also cancel all entries where `ScopeId == hostActivityInstanceId`.

Check the `BoundaryEventHandler` to see where host activity cancellation happens, and add scope cancellation there. Read the file first:

Read: `src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs`

Then add a method to cancel all entries in a scope. The key is: after the boundary handler cancels the host activity, it also needs to cancel all scoped child entries.

Add to `WorkflowInstance.cs`, a method that `BoundaryEventHandler` can call via `IBoundaryEventStateAccessor`:

```csharp
public async Task CancelScopeChildren(Guid scopeId)
{
    foreach (var entry in State.GetActiveActivities().Where(e => e.ScopeId == scopeId).ToList())
    {
        var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
        await activityInstance.Cancel($"Sub-process scope cancelled by boundary event");
        entry.MarkCompleted();
        LogScopeChildCancelled(entry.ActivityId, scopeId);
    }
}
```

Add `CancelScopeChildren` to `IBoundaryEventStateAccessor` interface and call it from the boundary handler after host activity cancellation.

Add LoggerMessage:

```csharp
[LoggerMessage(EventId = 1036, Level = LogLevel.Information,
    Message = "Scope child {ActivityId} cancelled (scope {ScopeId})")]
private partial void LogScopeChildCancelled(string activityId, Guid scopeId);
```

**Step 4: For error boundary on child tasks inside sub-process — propagate error to sub-process boundary**

When `FailActivityWithBoundaryCheck` is called for `sub_task`, it searches for a boundary error on `sub_task`. If not found, it should bubble up to the sub-process and check for boundary error on the sub-process.

Modify `FailActivityWithBoundaryCheck`: after the existing boundary error check fails, check if the activity is inside a sub-process (via ScopeId on its entry). If so, look for boundary error on the sub-process host activity. This is the error propagation logic.

Add after the `if (boundaryEvent is not null) { ... return; }` block:

```csharp
// Check if activity is inside a sub-process — propagate error to sub-process boundary
var activityEntry2 = State.Entries.Last(e => e.ActivityId == activityId);
if (activityEntry2.ScopeId.HasValue)
{
    var scopeEntry = State.Entries.First(e => e.ActivityInstanceId == activityEntry2.ScopeId.Value);
    var scopeDef = GetDefinitionForActivity(scopeEntry.ActivityId, definition);
    var parentDef = GetParentDefinition(scopeEntry.ActivityId, definition);
    var subProcessBoundary = parentDef.Activities
        .OfType<BoundaryErrorEvent>()
        .FirstOrDefault(b => b.AttachedToActivityId == scopeEntry.ActivityId
            && (b.ErrorCode == null || b.ErrorCode == errorState.Code.ToString()));

    if (subProcessBoundary is not null)
    {
        await CancelScopeChildren(activityEntry2.ScopeId.Value);
        await _boundaryHandler.HandleBoundaryErrorAsync(scopeEntry.ActivityId, subProcessBoundary, scopeEntry.ActivityInstanceId, parentDef);
        return;
    }
}
```

**Step 5: Run boundary tests — verify they pass**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~SubProcess_Boundary" --verbosity minimal`
Expected: 2 tests PASS.

**Step 6: Run ALL tests to verify no regression**

Run: `dotnet test src/Fleans/ --verbosity minimal`
Expected: All tests pass.

**Step 7: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs src/Fleans/Fleans.Application.Tests/SubProcessTests.cs
git commit -m "feat: boundary event cancellation on SubProcess with scope-based child cleanup"
```

---

## Task 11: Documentation — Update README + risk audit

**Files:**
- Modify: `README.md` (Sub-Process row)
- Modify: `docs/plans/2026-02-17-architectural-risk-audit.md` (Phase 2 items)

**Step 1: Update README**

Change the Sub-Process / Embedded Sub-Process row to `[x]` (or add it if not present).

**Step 2: Update risk audit**

Mark items 2.1, 2.2, 2.3 as done:

```
- [x] **2.1 — Tree-structured WorkflowDefinition**: SubProcess activity holds child Activities and SequenceFlows, implements IWorkflowDefinition. Recursive BpmnConverter parsing. *Done.*
- [x] **2.2 — Variable scope chain**: WorkflowVariablesState.ParentVariablesId chains scopes. GetVariable walks up. Writes go to local scope. *Done.*
- [x] **2.3 — Embedded Sub-Process**: SubProcess executes within same WorkflowInstance grain. ScopeId on ActivityInstanceEntry tracks nesting. Scope completion detection and boundary event cancellation. *Done.*
```

**Step 3: Commit**

```bash
git add README.md docs/plans/2026-02-17-architectural-risk-audit.md
git commit -m "docs: mark Phase 2 items 2.1–2.3 as implemented"
```
