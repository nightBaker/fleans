# Multi-Instance Activity (Parallel) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Allow any activity (ScriptTask, SubProcess, CallActivity) to execute N times in parallel with per-iteration variable scopes and optional output aggregation.

**Architecture:** Multi-instance is a loop characteristic on existing activities, not a new activity type. When an activity with `LoopCharacteristics` executes, `WorkflowInstance` spawns N iteration entries scoped under the host (like SubProcess). Each gets its own child variable scope. Scope completion aggregates outputs and cleans up child scopes.

**Tech Stack:** C# / Orleans / MSTest / BPMN XML parsing

**Design doc:** `docs/plans/2026-02-26-multi-instance-parallel-design.md`

---

### Task 1: Domain model — MultiInstanceLoopCharacteristics record

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/MultiInstanceLoopCharacteristics.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/Activity.cs`

**Step 1: Create the record**

Create `src/Fleans/Fleans.Domain/Activities/MultiInstanceLoopCharacteristics.cs`:

```csharp
namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record MultiInstanceLoopCharacteristics(
    [property: Id(0)] bool IsSequential,
    [property: Id(1)] int? LoopCardinality,
    [property: Id(2)] string? InputCollection,
    [property: Id(3)] string? InputDataItem,
    [property: Id(4)] string? OutputCollection,
    [property: Id(5)] string? OutputDataItem
);
```

**Step 2: Add property to Activity**

In `src/Fleans/Fleans.Domain/Activities/Activity.cs`, add a nullable property to the `Activity` record:

```csharp
[Id(1)]
public MultiInstanceLoopCharacteristics? LoopCharacteristics { get; init; }
```

**Step 3: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/MultiInstanceLoopCharacteristics.cs src/Fleans/Fleans.Domain/Activities/Activity.cs
git commit -m "feat: add MultiInstanceLoopCharacteristics domain model"
```

---

### Task 2: Add MultiInstanceIndex to ActivityInstanceEntry

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs`

**Step 1: Add property and constructor overload**

In `src/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs`:

Add a new property:

```csharp
[Id(6)]
public int? MultiInstanceIndex { get; private set; }
```

Add a new constructor:

```csharp
public ActivityInstanceEntry(Guid activityInstanceId, string activityId, Guid workflowInstanceId, Guid? scopeId, int multiInstanceIndex)
    : this(activityInstanceId, activityId, workflowInstanceId, scopeId)
{
    MultiInstanceIndex = multiInstanceIndex;
}
```

**Step 2: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 3: Run existing tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass (existing constructors unchanged, new property defaults to null)

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs
git commit -m "feat: add MultiInstanceIndex to ActivityInstanceEntry"
```

---

### Task 3: Add variable scope cleanup to WorkflowInstanceState

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs`

**Step 1: Add RemoveVariableStates method**

In `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs`, add:

```csharp
public void RemoveVariableStates(IEnumerable<Guid> variableStateIds)
{
    var idsToRemove = new HashSet<Guid>(variableStateIds);
    VariableStates.RemoveAll(vs => idsToRemove.Contains(vs.Id));
}
```

**Step 2: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs
git commit -m "feat: add RemoveVariableStates for multi-instance cleanup"
```

---

### Task 4: Write failing test — cardinality-based parallel multi-instance

**Files:**
- Create: `src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs`

**Step 1: Write the test**

Create `src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs`:

```csharp
using Fleans.Application.Grains;
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class MultiInstanceTests : WorkflowTestBase
{
    [TestMethod]
    public async Task MultiInstance_Cardinality_ShouldExecuteNTimes()
    {
        // Arrange — workflow: start → scriptTask (multi-instance x3) → end
        var start = new StartEvent("start");
        var script = new ScriptTask("script", "_context.result = \"done-\" + _context.loopCounter")
        {
            LoopCharacteristics = new MultiInstanceLoopCharacteristics(
                IsSequential: false,
                LoopCardinality: 3,
                InputCollection: null,
                InputDataItem: null,
                OutputCollection: null,
                OutputDataItem: null)
        };
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "miCardinalityTest",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("miCardinalityTest");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count, "No active activities should remain");

        // Script task should appear 3 times in completed activities (one per iteration)
        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.AreEqual(3, scriptCompletions, "Script task should have completed 3 times");
    }
}
```

**Step 2: Build and run test to verify it fails**

Run: `dotnet build src/Fleans/ && dotnet test src/Fleans/ --filter "MultiInstance_Cardinality_ShouldExecuteNTimes"`
Expected: FAIL — multi-instance logic not implemented yet, script executes once

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs
git commit -m "test: add failing test for cardinality-based multi-instance"
```

---

### Task 5: Implement OpenMultiInstanceScope and ExecuteWorkflow integration

This is the core implementation task.

**Files:**
- Modify: `src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`

**Step 1: Add OpenMultiInstanceScope to IWorkflowExecutionContext**

In `src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs`, add:

```csharp
ValueTask OpenMultiInstanceScope(Guid hostInstanceId, Activity activity, Guid parentVariablesId);
```

**Step 2: Add multi-instance check in ExecuteWorkflow**

In `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`, modify the `ExecuteWorkflow()` method. Inside the foreach loop, after getting `currentActivity`, add a multi-instance check before calling `ExecuteAsync`:

Replace:

```csharp
SetActivityRequestContext(activityId, activityState);
LogExecutingActivity(activityId, currentActivity.GetType().Name);
await currentActivity.ExecuteAsync(this, activityState, scopeDefinition);
```

With:

```csharp
SetActivityRequestContext(activityId, activityState);
LogExecutingActivity(activityId, currentActivity.GetType().Name);

// Check if this is a multi-instance host (not an iteration)
var entryForActivity = State.GetFirstActive(activityId);
if (currentActivity.LoopCharacteristics is { IsSequential: false } && entryForActivity?.MultiInstanceIndex is null)
{
    var instanceId = await activityState.GetActivityInstanceId();
    var variablesId = await activityState.GetVariablesStateId();
    await activityState.Execute(); // Mark as executing
    await OpenMultiInstanceScope(instanceId, currentActivity, variablesId);
}
else
{
    await currentActivity.ExecuteAsync(this, activityState, scopeDefinition);
}
```

**Step 3: Implement OpenMultiInstanceScope**

Add to `WorkflowInstance`:

```csharp
public async ValueTask OpenMultiInstanceScope(Guid hostInstanceId, Activity activity, Guid parentVariablesId)
{
    var loopChars = activity.LoopCharacteristics!;

    // Resolve iteration count
    int count;
    object[]? collectionItems = null;
    if (loopChars.LoopCardinality.HasValue)
    {
        count = loopChars.LoopCardinality.Value;
    }
    else if (loopChars.InputCollection is not null)
    {
        var collectionVar = await GetVariable(parentVariablesId, loopChars.InputCollection);
        if (collectionVar is IEnumerable<object> enumerable)
        {
            collectionItems = enumerable.ToArray();
            count = collectionItems.Length;
        }
        else
        {
            throw new InvalidOperationException(
                $"Multi-instance inputCollection '{loopChars.InputCollection}' must be a list/array");
        }
    }
    else
    {
        throw new InvalidOperationException(
            "Multi-instance must have either LoopCardinality or InputCollection");
    }

    LogMultiInstanceScopeOpened(activity.ActivityId, count);

    var newEntries = new List<ActivityInstanceEntry>();
    for (var i = 0; i < count; i++)
    {
        var childVariablesId = State.AddChildVariableState(parentVariablesId);

        // Set loopCounter
        dynamic loopVars = new ExpandoObject();
        loopVars.loopCounter = i;
        State.MergeState(childVariablesId, (ExpandoObject)loopVars);

        // Set inputDataItem if collection-driven
        if (collectionItems is not null && loopChars.InputDataItem is not null)
        {
            dynamic itemVars = new ExpandoObject();
            ((IDictionary<string, object?>)itemVars)[loopChars.InputDataItem] = collectionItems[i];
            State.MergeState(childVariablesId, (ExpandoObject)itemVars);
        }

        var iterationInstanceId = Guid.NewGuid();
        var iterationGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(iterationInstanceId);
        await iterationGrain.SetActivity(activity.ActivityId, activity.GetType().Name);
        await iterationGrain.SetVariablesId(childVariablesId);

        var iterationEntry = new ActivityInstanceEntry(
            iterationInstanceId, activity.ActivityId, State.Id, hostInstanceId, i);
        newEntries.Add(iterationEntry);
    }

    State.AddEntries(newEntries);
}
```

**Step 4: Add LoggerMessage**

Add near the other multi-instance / sub-process log messages:

```csharp
[LoggerMessage(EventId = 1021, Level = LogLevel.Information, Message = "Multi-instance scope opened for activity {ActivityId} with {IterationCount} iterations")]
private partial void LogMultiInstanceScopeOpened(string activityId, int iterationCount);
```

**Step 5: Run test**

Run: `dotnet test src/Fleans/ --filter "MultiInstance_Cardinality_ShouldExecuteNTimes"`
Expected: May still fail — scope completion not yet implemented. The iterations will execute but the host won't complete.

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "feat: implement OpenMultiInstanceScope for parallel iterations"
```

---

### Task 6: Implement multi-instance scope completion with output aggregation

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`

**Step 1: Extend CompleteFinishedSubProcessScopes**

In `CompleteFinishedSubProcessScopes()`, the current check is:

```csharp
if (activity is not SubProcess) continue;
```

Change to also handle multi-instance hosts:

```csharp
var isSubProcess = activity is SubProcess;
var isMultiInstanceHost = activity.LoopCharacteristics is not null
    && entry.MultiInstanceIndex is null;

if (!isSubProcess && !isMultiInstanceHost) continue;
```

After the existing sub-process completion block (the `activityInstance.Complete()` and `nextActivities` code), add multi-instance specific logic. The cleanest way is to add the aggregation and cleanup **before** calling `activityInstance.Complete()` when `isMultiInstanceHost` is true:

Replace the completion section inside the `foreach (var entry in ...)` block. After confirming all scope entries are completed:

```csharp
// All scope children are done
if (isMultiInstanceHost)
{
    // Aggregate output variables if configured
    var loopChars = activity.LoopCharacteristics!;
    if (loopChars.OutputDataItem is not null && loopChars.OutputCollection is not null)
    {
        var iterationEntries = State.Entries
            .Where(e => e.ScopeId == entry.ActivityInstanceId && e.MultiInstanceIndex.HasValue)
            .OrderBy(e => e.MultiInstanceIndex!.Value)
            .ToList();

        var outputList = new List<object?>();
        foreach (var iterEntry in iterationEntries)
        {
            var iterGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(iterEntry.ActivityInstanceId);
            var iterVarId = await iterGrain.GetVariablesStateId();
            var outputValue = State.GetVariable(iterVarId, loopChars.OutputDataItem);
            outputList.Add(outputValue);
        }

        // Set aggregated output on host's variable scope
        var hostGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
        var hostVarId = await hostGrain.GetVariablesStateId();
        dynamic outputVars = new ExpandoObject();
        ((IDictionary<string, object?>)outputVars)[loopChars.OutputCollection] = outputList;
        State.MergeState(hostVarId, (ExpandoObject)outputVars);
    }

    // Clean up child variable scopes
    var childVarIds = State.Entries
        .Where(e => e.ScopeId == entry.ActivityInstanceId && e.MultiInstanceIndex.HasValue)
        .Select(e =>
        {
            var g = _grainFactory.GetGrain<IActivityInstanceGrain>(e.ActivityInstanceId);
            return g.GetVariablesStateId().Result;
        })
        .ToList();
    State.RemoveVariableStates(childVarIds);
    LogMultiInstanceScopeCompleted(entry.ActivityId);
}
else
{
    LogSubProcessCompleted(entry.ActivityId);
}

// Complete the host/sub-process entry and transition
var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
await activityInstance.Complete();

var nextActivities = await activity.GetNextActivities(this, activityInstance, scopeDefinition);

var completedEntries = new List<ActivityInstanceEntry> { entry };
var newEntries = new List<ActivityInstanceEntry>();

foreach (var nextActivity in nextActivities)
{
    var newEntry = await CreateNextActivityEntry(activity, activityInstance, nextActivity, entry.ScopeId);
    newEntries.Add(newEntry);
}

State.CompleteEntries(completedEntries);
State.AddEntries(newEntries);
anyCompleted = true;
```

**Step 2: Add LoggerMessage**

```csharp
[LoggerMessage(EventId = 1022, Level = LogLevel.Information, Message = "Multi-instance scope completed for activity {ActivityId}")]
private partial void LogMultiInstanceScopeCompleted(string activityId);
```

**Step 3: Run test**

Run: `dotnet test src/Fleans/ --filter "MultiInstance_Cardinality_ShouldExecuteNTimes"`
Expected: PASS

**Step 4: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "feat: implement multi-instance scope completion with output aggregation"
```

---

### Task 7: Write and pass collection-based test

**Files:**
- Modify: `src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs`

**Step 1: Add collection-based test**

Add to `MultiInstanceTests.cs`:

```csharp
[TestMethod]
public async Task MultiInstance_Collection_ShouldIterateOverItems()
{
    // Arrange — workflow: start → setItems → script (multi-instance over items) → end
    var start = new StartEvent("start");
    var setItems = new ScriptTask("setItems",
        "_context.items = new System.Collections.Generic.List<object> { \"A\", \"B\", \"C\" }");
    var script = new ScriptTask("script", "_context.result = \"processed-\" + _context.item")
    {
        LoopCharacteristics = new MultiInstanceLoopCharacteristics(
            IsSequential: false,
            LoopCardinality: null,
            InputCollection: "items",
            InputDataItem: "item",
            OutputCollection: "results",
            OutputDataItem: "result")
    };
    var end = new EndEvent("end");

    var workflow = new WorkflowDefinition
    {
        WorkflowId = "miCollectionTest",
        Activities = [start, setItems, script, end],
        SequenceFlows =
        [
            new SequenceFlow("s1", start, setItems),
            new SequenceFlow("s2", setItems, script),
            new SequenceFlow("s3", script, end)
        ]
    };

    var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
    await factory.DeployWorkflow(workflow, "<xml/>");

    var instance = await factory.CreateWorkflowInstanceGrain("miCollectionTest");
    var instanceId = instance.GetPrimaryKey();

    // Act
    await instance.StartWorkflow();

    // Assert
    var snapshot = await QueryService.GetStateSnapshot(instanceId);
    Assert.IsNotNull(snapshot);
    Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

    // Verify output aggregation — results should be ordered by iteration index
    var rootVars = snapshot.VariableStates
        .SelectMany(vs => vs.Variables)
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    Assert.IsTrue(rootVars.ContainsKey("results"), "Output collection 'results' should exist");
}
```

**Step 2: Run test**

Run: `dotnet test src/Fleans/ --filter "MultiInstance_Collection_ShouldIterateOverItems"`
Expected: PASS (implementation already handles collections)

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs
git commit -m "test: add collection-based multi-instance test with output aggregation"
```

---

### Task 8: Write and pass variable isolation + cleanup test

**Files:**
- Modify: `src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs`

**Step 1: Add test**

```csharp
[TestMethod]
public async Task MultiInstance_ShouldCleanupChildVariableScopes()
{
    // Arrange
    var start = new StartEvent("start");
    var script = new ScriptTask("script", "_context.iterResult = \"val\"")
    {
        LoopCharacteristics = new MultiInstanceLoopCharacteristics(
            IsSequential: false,
            LoopCardinality: 3,
            InputCollection: null,
            InputDataItem: null,
            OutputCollection: null,
            OutputDataItem: null)
    };
    var end = new EndEvent("end");

    var workflow = new WorkflowDefinition
    {
        WorkflowId = "miCleanupTest",
        Activities = [start, script, end],
        SequenceFlows =
        [
            new SequenceFlow("s1", start, script),
            new SequenceFlow("s2", script, end)
        ]
    };

    var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
    await factory.DeployWorkflow(workflow, "<xml/>");

    var instance = await factory.CreateWorkflowInstanceGrain("miCleanupTest");
    var instanceId = instance.GetPrimaryKey();

    // Act
    await instance.StartWorkflow();

    // Assert
    var snapshot = await QueryService.GetStateSnapshot(instanceId);
    Assert.IsNotNull(snapshot);
    Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

    // Child variable scopes should be cleaned up — only root scope should remain
    Assert.AreEqual(1, snapshot.VariableStates.Count,
        "Only root variable scope should remain after multi-instance cleanup");

    // iterResult should NOT leak to parent scope
    var rootVars = snapshot.VariableStates[0].Variables;
    Assert.IsFalse(rootVars.ContainsKey("iterResult"),
        "Child iteration variables should not leak to parent scope");
}
```

**Step 2: Run test**

Run: `dotnet test src/Fleans/ --filter "MultiInstance_ShouldCleanupChildVariableScopes"`
Expected: PASS

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs
git commit -m "test: add variable isolation and cleanup test for multi-instance"
```

---

### Task 9: Write and pass failure test

**Files:**
- Modify: `src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs`

**Step 1: Add test**

```csharp
[TestMethod]
public async Task MultiInstance_IterationFails_ShouldFailHostAndTriggerBoundary()
{
    // Arrange — workflow: start → task (multi-instance x3) → happyEnd
    //   with errorBoundary on task → errorHandler → end2
    var start = new StartEvent("start");
    var task = new TaskActivity("task")
    {
        LoopCharacteristics = new MultiInstanceLoopCharacteristics(
            IsSequential: false,
            LoopCardinality: 3,
            InputCollection: null,
            InputDataItem: null,
            OutputCollection: null,
            OutputDataItem: null)
    };
    var happyEnd = new EndEvent("happyEnd");
    var errorBoundary = new BoundaryErrorEvent("errorBoundary", "task", null);
    var errorHandler = new TaskActivity("errorHandler");
    var end2 = new EndEvent("end2");

    var workflow = new WorkflowDefinition
    {
        WorkflowId = "miFailTest",
        Activities = [start, task, happyEnd, errorBoundary, errorHandler, end2],
        SequenceFlows =
        [
            new SequenceFlow("s1", start, task),
            new SequenceFlow("s2", task, happyEnd),
            new SequenceFlow("s3", errorBoundary, errorHandler),
            new SequenceFlow("s4", errorHandler, end2)
        ]
    };

    var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
    await factory.DeployWorkflow(workflow, "<xml/>");

    var instance = await factory.CreateWorkflowInstanceGrain("miFailTest");
    var instanceId = instance.GetPrimaryKey();

    // Act — start workflow, then fail one of the iteration instances
    await instance.StartWorkflow();

    // Get one of the iteration activity instances
    var snapshot = await QueryService.GetStateSnapshot(instanceId);
    Assert.IsNotNull(snapshot);
    var iterationActivity = snapshot.ActiveActivities.First(a => a.ActivityId == "task");

    // Fail via the workflow instance grain using the iteration's instance ID
    await instance.FailActivity("task", new Exception("Iteration failed"));

    // Assert — error boundary should have fired
    var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
    Assert.IsNotNull(finalSnapshot);

    Assert.IsTrue(
        finalSnapshot.ActiveActivities.Any(a => a.ActivityId == "errorHandler"),
        "Error handler should be active after multi-instance iteration failure");

    Assert.IsTrue(
        finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "errorBoundary"),
        "Error boundary event should be completed");
}
```

Note: This test uses `TaskActivity` for the multi-instance host, which is the unsupported-for-external-completion type. However, the host itself never needs external completion — it completes via scope detection. The iterations are `TaskActivity` instances that we manually fail. This is valid for testing the failure path. The restriction on TaskActivity is about *completing* iterations, not *failing* them.

**Step 2: Run test**

Run: `dotnet test src/Fleans/ --filter "MultiInstance_IterationFails_ShouldFailHostAndTriggerBoundary"`
Expected: PASS — the existing `FailActivityWithBoundaryCheck` should handle this since the iteration has no local boundary handler, and the error should bubble to the host via `FindBoundaryErrorHandler`.

If this test fails because the error doesn't bubble correctly, the fix is in `FailActivityWithBoundaryCheck`: when a multi-instance iteration (identified by `MultiInstanceIndex != null`) fails, cancel sibling iterations via `CancelScopeChildren(scopeId)` and fail the host entry, which triggers `FindBoundaryErrorHandler` on the host.

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs
git commit -m "test: add multi-instance iteration failure test"
```

---

### Task 10: BPMN parsing for multiInstanceLoopCharacteristics

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs`
- Test: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/` (new test file)

**Step 1: Add parsing helper**

In `BpmnConverter.cs`, add a private method (near the end of the class, with other helper methods):

```csharp
private static MultiInstanceLoopCharacteristics? ParseMultiInstanceLoopCharacteristics(XElement activityElement)
{
    var miElement = activityElement.Element(Bpmn + "multiInstanceLoopCharacteristics");
    if (miElement is null) return null;

    var isSequential = bool.TryParse(miElement.Attribute("isSequential")?.Value, out var seq) && seq;

    // Parse loopCardinality
    int? loopCardinality = null;
    var cardinalityEl = miElement.Element(Bpmn + "loopCardinality");
    if (cardinalityEl is not null && int.TryParse(cardinalityEl.Value.Trim(), out var card))
        loopCardinality = card;

    // Parse collection — Camunda extension attributes
    var camundaNs = (XNamespace)"http://camunda.org/schema/modeler/1.0";
    var inputCollection = miElement.Attribute(Zeebe + "collection")?.Value
        ?? miElement.Attribute("collection")?.Value;
    var inputDataItem = miElement.Attribute(Zeebe + "elementVariable")?.Value
        ?? miElement.Attribute("elementVariable")?.Value;

    // Parse output — Camunda extension attributes
    var outputCollection = miElement.Attribute(Zeebe + "outputCollection")?.Value
        ?? miElement.Attribute("outputCollection")?.Value;
    var outputDataItem = miElement.Attribute(Zeebe + "outputElement")?.Value
        ?? miElement.Attribute("outputElement")?.Value;

    return new MultiInstanceLoopCharacteristics(
        isSequential, loopCardinality, inputCollection, inputDataItem, outputCollection, outputDataItem);
}
```

**Step 2: Apply after each activity type**

In `ParseActivities()`, after creating each activity that could be multi-instance (scriptTask, callActivity, subProcess), apply the loop characteristics.

Since `Activity.LoopCharacteristics` is an `init` property, it must be set during creation. The cleanest approach is to add a helper that creates the activity with loop characteristics:

For scriptTask (after line 198):

```csharp
var activity = new ScriptTask(id, script, scriptFormat)
{
    LoopCharacteristics = ParseMultiInstanceLoopCharacteristics(scriptTask)
};
```

For subProcess (after line 274):

```csharp
var activity = new SubProcess(id)
{
    Activities = childActivities,
    SequenceFlows = childSequenceFlows,
    LoopCharacteristics = ParseMultiInstanceLoopCharacteristics(subProcessEl)
};
```

For callActivity (after line ~310, the existing `new CallActivity(...)` call — CallActivity's constructor takes positional params so use `with`):

After the existing callActivity creation, add:

```csharp
var miChars = ParseMultiInstanceLoopCharacteristics(callActivityEl);
if (miChars is not null)
    activity = activity with { LoopCharacteristics = miChars };
```

**Step 3: Write test**

Create `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/MultiInstanceParsingTests.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Infrastructure.Bpmn;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class MultiInstanceParsingTests
{
    [TestMethod]
    public async Task Parse_ScriptTask_WithMultiInstanceCardinality()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""script"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
      <multiInstanceLoopCharacteristics isSequential=""false"">
        <loopCardinality>5</loopCardinality>
      </multiInstanceLoopCharacteristics>
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""script"" />
    <sequenceFlow id=""f2"" sourceRef=""script"" targetRef=""end"" />
  </process>
</definitions>";

        var converter = new Fleans.Infrastructure.Bpmn.BpmnConverter();
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        var scriptActivity = workflow.Activities.OfType<ScriptTask>().First();
        Assert.IsNotNull(scriptActivity.LoopCharacteristics, "Should have loop characteristics");
        Assert.IsFalse(scriptActivity.LoopCharacteristics.IsSequential);
        Assert.AreEqual(5, scriptActivity.LoopCharacteristics.LoopCardinality);
        Assert.IsNull(scriptActivity.LoopCharacteristics.InputCollection);
    }

    [TestMethod]
    public async Task Parse_ScriptTask_WithMultiInstanceCollection()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:zeebe=""http://camunda.org/schema/zeebe/1.0""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""script"" scriptFormat=""csharp"">
      <script>_context.result = _context.item</script>
      <multiInstanceLoopCharacteristics isSequential=""false""
        zeebe:collection=""items"" zeebe:elementVariable=""item""
        zeebe:outputCollection=""results"" zeebe:outputElement=""result"" />
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""script"" />
    <sequenceFlow id=""f2"" sourceRef=""script"" targetRef=""end"" />
  </process>
</definitions>";

        var converter = new Fleans.Infrastructure.Bpmn.BpmnConverter();
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        var scriptActivity = workflow.Activities.OfType<ScriptTask>().First();
        Assert.IsNotNull(scriptActivity.LoopCharacteristics);
        Assert.AreEqual("items", scriptActivity.LoopCharacteristics.InputCollection);
        Assert.AreEqual("item", scriptActivity.LoopCharacteristics.InputDataItem);
        Assert.AreEqual("results", scriptActivity.LoopCharacteristics.OutputCollection);
        Assert.AreEqual("result", scriptActivity.LoopCharacteristics.OutputDataItem);
    }
}
```

**Step 4: Run tests**

Run: `dotnet test src/Fleans/ --filter "MultiInstanceParsingTests"`
Expected: PASS

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/MultiInstanceParsingTests.cs
git commit -m "feat: parse multiInstanceLoopCharacteristics from BPMN XML"
```

---

### Task 11: Update README BPMN elements table

**Files:**
- Modify: `README.md`

**Step 1: Add Multi-Instance Activity to the BPMN elements table**

Find the elements table and add a row for Multi-Instance Activity (Parallel). Mark it as supported.

**Step 2: Commit**

```bash
git add README.md
git commit -m "docs: add multi-instance activity to BPMN elements table"
```

---

### Task 12: Manual test fixtures

**Files:**
- Create: `tests/manual/13-multi-instance/parallel-collection.bpmn`
- Create: `tests/manual/13-multi-instance/parallel-cardinality.bpmn`
- Create: `tests/manual/13-multi-instance/test-plan.md`

**Step 1: Create test-plan.md**

Create `tests/manual/13-multi-instance/test-plan.md`:

```markdown
# Multi-Instance Activity (Parallel)

## Scenario 13a: Collection-based parallel

Tests parallel multi-instance over a collection with output aggregation.

### Prerequisites
- None

### 1. Deploy the workflow
- Open Workflows page
- Click "Create New"
- Import `parallel-collection.bpmn` via drag-drop
- Click Deploy, confirm

### 2. Start an instance
- On Workflows page, click "Start" for `parallel-collection-test`
- Navigate to the instance viewer

### 3. Verify outcome
- [ ] Instance status: Completed
- [ ] Completed activities: start, setItems, reviewTasks (3 iterations), end
- [ ] Variables tab: `results` contains `["reviewed-A","reviewed-B","reviewed-C"]`
- [ ] No error activities

---

## Scenario 13b: Cardinality-based parallel

Tests parallel multi-instance with fixed loop count.

### Prerequisites
- None

### 1. Deploy the workflow
- Open Workflows page
- Click "Create New"
- Import `parallel-cardinality.bpmn` via drag-drop
- Click Deploy, confirm

### 2. Start an instance
- On Workflows page, click "Start" for `parallel-cardinality-test`
- Navigate to the instance viewer

### 3. Verify outcome
- [ ] Instance status: Completed
- [ ] Completed activities: start, repeatTask (3 iterations), end
- [ ] No error activities
```

**Step 2: Create BPMN fixtures**

Create `tests/manual/13-multi-instance/parallel-collection.bpmn` and `parallel-cardinality.bpmn` with valid BPMN XML including:
- `scriptFormat="csharp"` on all script tasks
- `<bpmndi:BPMNDiagram>` section
- `<multiInstanceLoopCharacteristics>` on the multi-instance task
- Short, simple scripts

**Step 3: Commit**

```bash
git add tests/manual/13-multi-instance/
git commit -m "test: add manual test fixtures for multi-instance parallel"
```

---

### Task 13: Update architectural risk audit checklist

**Files:**
- Modify: `docs/plans/2026-02-17-architectural-risk-audit.md`

**Step 1: Mark item 2.4 as done**

Change:
```
- [ ] **2.4 — Multi-Instance Activity (parallel)**:
```

To:
```
- [x] **2.4 — Multi-Instance Activity (parallel)**:
```

**Step 2: Commit**

```bash
git add docs/plans/2026-02-17-architectural-risk-audit.md
git commit -m "docs: mark multi-instance parallel as completed in risk audit"
```
