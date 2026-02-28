# Multi-Instance Activity Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Allow any activity to execute N times (parallel or sequential), each with its own variable scope, with optional output aggregation — implementing BPMN `multiInstanceLoopCharacteristics`.

**Architecture:** `MultiInstanceActivity` wraps any existing activity. On execution, the host opens a scope and spawns iteration entries (all at once for parallel, one-at-a-time for sequential). Each iteration executes the inner activity with its own child variable scope. Scope completion aggregates output, cleans up child scopes, and completes the host. The `activityInstanceId` is plumbed through `CompleteActivity`/`FailActivity` to disambiguate concurrent iterations sharing the same `ActivityId`.

**Tech Stack:** C# / Orleans / MSTest / BPMN XML parsing

**Design doc:** `docs/plans/2026-02-27-multi-instance-activity-design.md`

---

### Task 1: Domain model — MultiInstanceActivity, command, entry field, state cleanup

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/MultiInstanceActivity.cs`
- Modify: `src/Fleans/Fleans.Domain/ExecutionCommands.cs`
- Modify: `src/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs`
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs`

**Step 1: Create MultiInstanceActivity**

Create `src/Fleans/Fleans.Domain/Activities/MultiInstanceActivity.cs`:

```csharp
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record MultiInstanceActivity : Activity
{
    [Id(1)] public Activity InnerActivity { get; init; }
    [Id(2)] public bool IsSequential { get; init; }
    [Id(3)] public int? LoopCardinality { get; init; }
    [Id(4)] public string? InputCollection { get; init; }
    [Id(5)] public string? InputDataItem { get; init; }
    [Id(6)] public string? OutputCollection { get; init; }
    [Id(7)] public string? OutputDataItem { get; init; }

    public MultiInstanceActivity(
        string ActivityId,
        Activity InnerActivity,
        bool IsSequential = false,
        int? LoopCardinality = null,
        string? InputCollection = null,
        string? InputDataItem = null,
        string? OutputCollection = null,
        string? OutputDataItem = null) : base(ActivityId)
    {
        this.InnerActivity = InnerActivity;
        this.IsSequential = IsSequential;
        this.LoopCardinality = LoopCardinality;
        this.InputCollection = InputCollection;
        this.InputDataItem = InputDataItem;
        this.OutputCollection = OutputCollection;
        this.OutputDataItem = OutputDataItem;
    }

    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        var variablesId = await activityContext.GetVariablesStateId();
        commands.Add(new OpenMultiInstanceCommand(this, variablesId));
        return commands;
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

**Step 2: Add OpenMultiInstanceCommand**

In `src/Fleans/Fleans.Domain/ExecutionCommands.cs`, add at the end before the closing of the namespace (after `CompleteWorkflowCommand`):

```csharp
[GenerateSerializer]
public record OpenMultiInstanceCommand(
    [property: Id(0)] MultiInstanceActivity MultiInstanceActivity,
    [property: Id(1)] Guid ParentVariablesId) : IExecutionCommand;
```

**Step 3: Add MultiInstanceIndex and MultiInstanceTotal to ActivityInstanceEntry**

In `src/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs`:

Add two properties after `ScopeId` (after line 32):

```csharp
    [Id(6)]
    public int? MultiInstanceIndex { get; private set; }

    [Id(7)]
    public int? MultiInstanceTotal { get; private set; }
```

Add a new constructor after the existing one (after line 12):

```csharp
    public ActivityInstanceEntry(Guid activityInstanceId, string activityId, Guid workflowInstanceId, Guid? scopeId, int multiInstanceIndex)
        : this(activityInstanceId, activityId, workflowInstanceId, scopeId)
    {
        MultiInstanceIndex = multiInstanceIndex;
    }
```

Add a setter for MultiInstanceTotal (after `MarkCompleted`):

```csharp
    public void SetMultiInstanceTotal(int total) => MultiInstanceTotal = total;
```

**Step 4: Add RemoveVariableStates to WorkflowInstanceState**

In `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs`, add after `AddEntries` (after line 133):

```csharp
    public void RemoveVariableStates(IEnumerable<Guid> variableStateIds)
    {
        var idsToRemove = new HashSet<Guid>(variableStateIds);
        VariableStates.RemoveAll(vs => idsToRemove.Contains(vs.Id));
    }
```

**Step 5: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds (no consumers of new types yet)

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/MultiInstanceActivity.cs \
  src/Fleans/Fleans.Domain/ExecutionCommands.cs \
  src/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs \
  src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs
git commit -m "feat: add MultiInstanceActivity domain model, command, entry fields, state cleanup"
```

---

### Task 2: Plumb activityInstanceId through CompleteActivity and FailActivity

The script handler and condition evaluator already have `ActivityInstanceId` from the event. With multi-instance, multiple iterations share the same `ActivityId`, so `GetFirstActive(activityId)` is ambiguous. We add instance-specific overloads.

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.ActivityLifecycle.cs`
- Modify: `src/Fleans/Fleans.Application/Events/Handlers/WorkflowExecuteScriptEventHandler.cs`
- Modify: `src/Fleans/Fleans.Application/Events/Handlers/WorfklowEvaluateConditionEventHandler.cs`

**Step 1: Add overloads to IWorkflowInstanceGrain**

In `src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs`, add after line 28 (`Task FailActivity(...)`):

```csharp
    Task CompleteActivity(string activityId, Guid activityInstanceId, ExpandoObject variables);
    Task FailActivity(string activityId, Guid activityInstanceId, Exception exception);
```

**Step 2: Implement overloads in WorkflowInstance.ActivityLifecycle.cs**

In `src/Fleans/Fleans.Application/Grains/WorkflowInstance.ActivityLifecycle.cs`:

Rename the existing private `CompleteActivityState` method (line 36) to accept an optional `activityInstanceId`:

Replace the existing `CompleteActivityState` method signature (line 36):
```csharp
    private async Task CompleteActivityState(string activityId, ExpandoObject variables)
```
With:
```csharp
    private async Task CompleteActivityState(string activityId, ExpandoObject variables, Guid? activityInstanceId = null)
```

And replace the entry lookup (line 38):
```csharp
        var entry = State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");
```
With:
```csharp
        var entry = activityInstanceId.HasValue
            ? State.GetActiveEntry(activityInstanceId.Value)
            : State.GetFirstActive(activityId)
                ?? throw new InvalidOperationException("Active activity not found");
```

Now add the new public overloads. After the existing `CompleteActivity` method (after line 23):

```csharp
    public async Task CompleteActivity(string activityId, Guid activityInstanceId, ExpandoObject variables)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogCompletingActivity(activityId);
        await CompleteActivityState(activityId, variables, activityInstanceId);
        await ExecuteWorkflow();
        await _state.WriteStateAsync();
    }
```

Similarly, rename `FailActivityState` to accept optional `activityInstanceId`:

Replace the existing `FailActivityState` method signature (line 112):
```csharp
    private async Task FailActivityState(string activityId, Exception exception)
```
With:
```csharp
    private async Task FailActivityState(string activityId, Exception exception, Guid? activityInstanceId = null)
```

And replace the entry lookup (line 114):
```csharp
        var entry = State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");
```
With:
```csharp
        var entry = activityInstanceId.HasValue
            ? State.GetActiveEntry(activityInstanceId.Value)
            : State.GetFirstActive(activityId)
                ?? throw new InvalidOperationException("Active activity not found");
```

Update `FailActivityWithBoundaryCheck` to accept optional `activityInstanceId`:

Replace its signature (line 262):
```csharp
    private async Task FailActivityWithBoundaryCheck(string activityId, Exception exception)
```
With:
```csharp
    private async Task FailActivityWithBoundaryCheck(string activityId, Exception exception, Guid? activityInstanceId = null)
```

And pass it through to `FailActivityState` (line 264):
```csharp
        await FailActivityState(activityId, exception);
```
With:
```csharp
        await FailActivityState(activityId, exception, activityInstanceId);
```

Add the new public `FailActivity` overload after the existing one (after line 34):

```csharp
    public async Task FailActivity(string activityId, Guid activityInstanceId, Exception exception)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogFailingActivity(activityId);
        await FailActivityWithBoundaryCheck(activityId, exception, activityInstanceId);
        await _state.WriteStateAsync();
    }
```

**Step 3: Update script handler to pass activityInstanceId**

In `src/Fleans/Fleans.Application/Events/Handlers/WorkflowExecuteScriptEventHandler.cs`, replace line 60:

```csharp
            await workflowInstance.CompleteActivity(item.ActivityId, result);
```

With:

```csharp
            await workflowInstance.CompleteActivity(item.ActivityId, item.ActivityInstanceId, result);
```

And replace line 65:

```csharp
            await workflowInstance.FailActivity(item.ActivityId, ex);
```

With:

```csharp
            await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
```

**Step 4: Update condition evaluator to pass activityInstanceId**

In `src/Fleans/Fleans.Application/Events/Handlers/WorfklowEvaluateConditionEventHandler.cs`, replace line 70:

```csharp
            await workflowInstance.FailActivity(item.ActivityId, ex);
```

With:

```csharp
            await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
```

**Step 5: Build and run all tests**

Run: `dotnet build src/Fleans/ && dotnet test src/Fleans/`
Expected: Build succeeds, all existing tests pass (existing callers use the original overloads)

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs \
  src/Fleans/Fleans.Application/Grains/WorkflowInstance.ActivityLifecycle.cs \
  src/Fleans/Fleans.Application/Events/Handlers/WorkflowExecuteScriptEventHandler.cs \
  src/Fleans/Fleans.Application/Events/Handlers/WorfklowEvaluateConditionEventHandler.cs
git commit -m "feat: plumb activityInstanceId through CompleteActivity and FailActivity"
```

---

### Task 3: Write failing test — cardinality-based parallel multi-instance

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
    public async Task ParallelCardinality_ShouldExecuteNTimes()
    {
        // Arrange — workflow: start → script (MI x3) → end
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"done-\" + _context.loopCounter"),
            IsSequential: false,
            LoopCardinality: 3);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-cardinality-test",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("mi-cardinality-test");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count, "No active activities should remain");

        // Script should appear 3 times in completed (iterations) + 1 time for host = check >=3
        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.IsTrue(scriptCompletions >= 3, $"Script should have completed at least 3 times, got {scriptCompletions}");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Fleans/ --filter "ParallelCardinality_ShouldExecuteNTimes"`
Expected: FAIL — `OpenMultiInstanceCommand` is not handled in `ProcessCommands`

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs
git commit -m "test: add failing test for cardinality-based parallel multi-instance"
```

---

### Task 4: Implement MI execution — OpenMultiInstanceScope, ProcessCommands, ExecuteWorkflow loop

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs`

**Step 1: Add OpenMultiInstanceCommand case to ProcessCommands**

In `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs`, inside `ProcessCommands` switch statement, add a new case after the `CompleteWorkflowCommand` case (before the closing `}` of the switch, around line 89):

```csharp
                case OpenMultiInstanceCommand mi:
                    await OpenMultiInstanceScope(entry.ActivityInstanceId, mi.MultiInstanceActivity, mi.ParentVariablesId);
                    break;
```

**Step 2: Implement OpenMultiInstanceScope**

Add this method to `WorkflowInstance.Execution.cs` (after `OpenSubProcessScope`, around line 155):

```csharp
    private async Task OpenMultiInstanceScope(Guid hostInstanceId, MultiInstanceActivity mi, Guid parentVariablesId)
    {
        // Resolve iteration count and collection items
        int count;
        IList<object>? collectionItems = null;

        if (mi.LoopCardinality.HasValue)
        {
            count = mi.LoopCardinality.Value;
        }
        else if (mi.InputCollection is not null)
        {
            var collectionVar = await GetVariable(parentVariablesId, mi.InputCollection);
            if (collectionVar is IList<object> list)
            {
                collectionItems = list;
                count = list.Count;
            }
            else if (collectionVar is System.Collections.IEnumerable enumerable)
            {
                collectionItems = enumerable.Cast<object>().ToList();
                count = collectionItems.Count;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Multi-instance inputCollection '{mi.InputCollection}' must resolve to a list/array, got: {collectionVar?.GetType().Name ?? "null"}");
            }
        }
        else
        {
            throw new InvalidOperationException(
                "Multi-instance must have either LoopCardinality or InputCollection");
        }

        // Set total on host entry
        var hostEntry = State.GetActiveEntry(hostInstanceId);
        hostEntry.SetMultiInstanceTotal(count);

        LogMultiInstanceScopeOpened(mi.ActivityId, count, mi.IsSequential);

        // Handle empty collection — complete host immediately
        if (count == 0)
        {
            if (mi.OutputCollection is not null)
            {
                var hostGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId);
                var hostVarId = await hostGrain.GetVariablesStateId();
                dynamic emptyOutput = new System.Dynamic.ExpandoObject();
                ((IDictionary<string, object?>)emptyOutput)[mi.OutputCollection] = new List<object?>();
                State.MergeState(hostVarId, (System.Dynamic.ExpandoObject)emptyOutput);
            }
            var hostGrainForComplete = _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId);
            await hostGrainForComplete.Complete();
            return;
        }

        // Spawn iterations
        var iterationsToSpawn = mi.IsSequential ? 1 : count;
        await SpawnMultiInstanceIterations(hostInstanceId, mi, parentVariablesId, collectionItems, 0, iterationsToSpawn);
    }

    private async Task SpawnMultiInstanceIterations(
        Guid hostInstanceId,
        MultiInstanceActivity mi,
        Guid parentVariablesId,
        IList<object>? collectionItems,
        int startIndex,
        int count)
    {
        var newEntries = new List<ActivityInstanceEntry>();

        for (var i = startIndex; i < startIndex + count; i++)
        {
            var childVariablesId = State.AddChildVariableState(parentVariablesId);

            // Set loopCounter
            dynamic loopVars = new System.Dynamic.ExpandoObject();
            ((IDictionary<string, object?>)loopVars)["loopCounter"] = i;
            State.MergeState(childVariablesId, (System.Dynamic.ExpandoObject)loopVars);

            // Set inputDataItem if collection-driven
            if (collectionItems is not null && mi.InputDataItem is not null)
            {
                dynamic itemVars = new System.Dynamic.ExpandoObject();
                ((IDictionary<string, object?>)itemVars)[mi.InputDataItem] = collectionItems[i];
                State.MergeState(childVariablesId, (System.Dynamic.ExpandoObject)itemVars);
            }

            var iterationInstanceId = Guid.NewGuid();
            var iterationGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(iterationInstanceId);
            await iterationGrain.SetActivity(mi.ActivityId, mi.InnerActivity.GetType().Name);
            await iterationGrain.SetVariablesId(childVariablesId);

            var iterationEntry = new ActivityInstanceEntry(
                iterationInstanceId, mi.ActivityId, State.Id, hostInstanceId, i);
            newEntries.Add(iterationEntry);
        }

        State.AddEntries(newEntries);
    }
```

**Step 3: Modify ExecuteWorkflow to handle MI iterations**

In the `ExecuteWorkflow` method, the inner foreach loop (around line 28-38) currently does:

```csharp
                var activityId = await activityState.GetActivityId();
                var scopeDefinition = definition.GetScopeForActivity(activityId);
                var currentActivity = scopeDefinition.GetActivity(activityId);
                SetActivityRequestContext(activityId, activityState);
                LogExecutingActivity(activityId, currentActivity.GetType().Name);
                var commands = await currentActivity.ExecuteAsync(this, activityState, scopeDefinition);
                var currentEntry = State.GetActiveEntry(activityState.GetPrimaryKey());
                await ProcessCommands(commands, currentEntry, activityState);
```

Replace it with:

```csharp
                var activityId = await activityState.GetActivityId();
                var scopeDefinition = definition.GetScopeForActivity(activityId);
                var currentActivity = scopeDefinition.GetActivity(activityId);
                SetActivityRequestContext(activityId, activityState);

                // For MI iterations, execute the inner activity instead of the wrapper
                Activity activityToExecute = currentActivity;
                if (currentActivity is MultiInstanceActivity mi)
                {
                    var entryBeforeExec = State.GetActiveEntry(activityState.GetPrimaryKey());
                    if (entryBeforeExec.MultiInstanceIndex is not null)
                        activityToExecute = mi.InnerActivity;
                }

                LogExecutingActivity(activityId, activityToExecute.GetType().Name);
                var commands = await activityToExecute.ExecuteAsync(this, activityState, scopeDefinition);

                // For MI iterations, filter out boundary registration commands
                // (boundaries apply to the host, not individual iterations)
                if (activityToExecute != currentActivity)
                {
                    commands = commands
                        .Where(c => c is not RegisterTimerCommand { IsBoundary: true }
                            and not RegisterMessageCommand { IsBoundary: true }
                            and not RegisterSignalCommand { IsBoundary: true })
                        .ToList();
                }

                var currentEntry = State.GetActiveEntry(activityState.GetPrimaryKey());
                await ProcessCommands(commands, currentEntry, activityState);
```

**Step 4: Add using for MultiInstanceActivity**

Ensure `using Fleans.Domain.Activities;` is present at the top of `WorkflowInstance.Execution.cs` (it should already be there from the existing code).

**Step 5: Add logging declarations**

In `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs`, add after the last `[LoggerMessage]` declaration (after `LogScopeChildCancelled`, around line 133):

```csharp
    [LoggerMessage(EventId = 1040, Level = LogLevel.Information,
        Message = "Multi-instance scope opened for activity {ActivityId}: {IterationCount} iterations, sequential={IsSequential}")]
    private partial void LogMultiInstanceScopeOpened(string activityId, int iterationCount, bool isSequential);

    [LoggerMessage(EventId = 1041, Level = LogLevel.Information,
        Message = "Multi-instance scope completed for activity {ActivityId}")]
    private partial void LogMultiInstanceScopeCompleted(string activityId);

    [LoggerMessage(EventId = 1042, Level = LogLevel.Debug,
        Message = "Multi-instance sequential: spawned next iteration {Index} for activity {ActivityId}")]
    private partial void LogMultiInstanceNextIteration(int index, string activityId);

    [LoggerMessage(EventId = 1043, Level = LogLevel.Debug,
        Message = "Multi-instance iteration {Index} completed for activity {ActivityId}")]
    private partial void LogMultiInstanceIterationCompleted(int index, string activityId);
```

**Step 6: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 7: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs \
  src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs
git commit -m "feat: implement OpenMultiInstanceScope and MI iteration execution"
```

---

### Task 5: Implement MI completion — TransitionToNextActivity skip, scope completion, output aggregation

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs`

**Step 1: Skip MI iterations in TransitionToNextActivity**

In `TransitionToNextActivity()`, inside the `if (await activityInstance.IsCompleted())` block, after the cancellation check (around line 194 `if (await activityInstance.IsCancelled()) continue;`), add:

```csharp
                // MI iterations don't transition to next activities — handled by scope completion
                if (entry.MultiInstanceIndex is not null)
                    continue;
```

**Step 2: Extend CompleteFinishedSubProcessScopes for MI hosts**

In `CompleteFinishedSubProcessScopes`, replace the check on line 247:

```csharp
                if (activity is not SubProcess) continue;
```

With:

```csharp
                var isSubProcess = activity is SubProcess;
                var isMultiInstanceHost = activity is MultiInstanceActivity
                    && entry.MultiInstanceIndex is null;

                if (!isSubProcess && !isMultiInstanceHost) continue;
```

Then, replace the block that checks if all scope children are done and completes the scope (lines 249-273). The current code is:

```csharp
                var scopeEntries = State.Entries.Where(e => e.ScopeId == entry.ActivityInstanceId).ToList();
                if (scopeEntries.Count == 0) continue;
                if (!scopeEntries.All(e => e.IsCompleted)) continue;

                // All scope children are done — complete the sub-process
                var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
                await activityInstance.Complete();
                LogSubProcessCompleted(entry.ActivityId);

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

Replace with:

```csharp
                var scopeEntries = State.Entries.Where(e => e.ScopeId == entry.ActivityInstanceId).ToList();
                if (scopeEntries.Count == 0) continue;

                if (isMultiInstanceHost)
                {
                    var completedMiResult = await TryCompleteMultiInstanceHost(
                        entry, (MultiInstanceActivity)activity, scopeDefinition, scopeEntries);
                    if (completedMiResult)
                        anyCompleted = true;
                    continue;
                }

                if (!scopeEntries.All(e => e.IsCompleted)) continue;

                // All scope children are done — complete the sub-process
                var activityInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(entry.ActivityInstanceId);
                await activityInstance.Complete();
                LogSubProcessCompleted(entry.ActivityId);

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

**Step 3: Implement TryCompleteMultiInstanceHost**

Add this method after `CompleteFinishedSubProcessScopes` (after line 274):

```csharp
    private async Task<bool> TryCompleteMultiInstanceHost(
        ActivityInstanceEntry hostEntry,
        MultiInstanceActivity mi,
        IWorkflowDefinition scopeDefinition,
        List<ActivityInstanceEntry> scopeEntries)
    {
        var completedIterations = scopeEntries.Where(e => e.IsCompleted).ToList();
        var activeIterations = scopeEntries.Where(e => !e.IsCompleted).ToList();
        var total = hostEntry.MultiInstanceTotal
            ?? throw new InvalidOperationException("MI host entry missing MultiInstanceTotal");

        // If there are active iterations, wait for them
        if (activeIterations.Count > 0)
            return false;

        // All spawned iterations are done
        if (completedIterations.Count < total)
        {
            // Sequential mode: spawn next iteration
            var nextIndex = completedIterations.Count;
            LogMultiInstanceNextIteration(nextIndex, mi.ActivityId);

            var hostGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(hostEntry.ActivityInstanceId);
            var parentVariablesId = await hostGrain.GetVariablesStateId();

            // Re-read collection for the next item
            IList<object>? collectionItems = null;
            if (mi.InputCollection is not null)
            {
                var collectionVar = await GetVariable(parentVariablesId, mi.InputCollection);
                if (collectionVar is IList<object> list)
                    collectionItems = list;
                else if (collectionVar is System.Collections.IEnumerable enumerable)
                    collectionItems = enumerable.Cast<object>().ToList();
            }

            await SpawnMultiInstanceIterations(hostEntry.ActivityInstanceId, mi, parentVariablesId, collectionItems, nextIndex, 1);
            return false; // host not completed yet
        }

        // All iterations done — aggregate output
        if (mi.OutputDataItem is not null && mi.OutputCollection is not null)
        {
            var iterationEntries = scopeEntries
                .Where(e => e.MultiInstanceIndex.HasValue)
                .OrderBy(e => e.MultiInstanceIndex!.Value)
                .ToList();

            var outputList = new List<object?>();
            foreach (var iterEntry in iterationEntries)
            {
                var iterGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(iterEntry.ActivityInstanceId);
                var iterVarId = await iterGrain.GetVariablesStateId();
                var outputValue = State.GetVariable(iterVarId, mi.OutputDataItem);
                outputList.Add(outputValue);
            }

            var hostGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(hostEntry.ActivityInstanceId);
            var hostVarId = await hostGrain.GetVariablesStateId();
            dynamic outputVars = new System.Dynamic.ExpandoObject();
            ((IDictionary<string, object?>)outputVars)[mi.OutputCollection] = outputList;
            State.MergeState(hostVarId, (System.Dynamic.ExpandoObject)outputVars);
        }

        // Clean up child variable scopes
        var childVarIds = new List<Guid>();
        foreach (var iterEntry in scopeEntries.Where(e => e.MultiInstanceIndex.HasValue))
        {
            var iterGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(iterEntry.ActivityInstanceId);
            childVarIds.Add(await iterGrain.GetVariablesStateId());
        }
        State.RemoveVariableStates(childVarIds);

        // Complete host
        var hostGrainForComplete = _grainFactory.GetGrain<IActivityInstanceGrain>(hostEntry.ActivityInstanceId);
        await hostGrainForComplete.Complete();
        LogMultiInstanceScopeCompleted(mi.ActivityId);

        var nextActivities = await mi.GetNextActivities(this, hostGrainForComplete, scopeDefinition);

        var completedEntries = new List<ActivityInstanceEntry> { hostEntry };
        var newEntries = new List<ActivityInstanceEntry>();

        foreach (var nextActivity in nextActivities)
        {
            var newEntry = await CreateNextActivityEntry(mi, hostGrainForComplete, nextActivity, hostEntry.ScopeId);
            newEntries.Add(newEntry);
        }

        State.CompleteEntries(completedEntries);
        State.AddEntries(newEntries);
        return true;
    }
```

**Step 4: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs
git commit -m "feat: implement MI scope completion with parallel/sequential support and output aggregation"
```

---

### Task 6: Run first test and write additional tests

**Step 1: Run cardinality test**

Run: `dotnet test src/Fleans/ --filter "ParallelCardinality_ShouldExecuteNTimes"`
Expected: PASS

If it fails, debug and fix. Common issues:
- Missing `using` directives
- `GetScopeForActivity` not finding activities because MI wrapper is the scope entry (should be fine since MI shares ActivityId)
- Scope completion not detecting MI host

**Step 2: Run all existing tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 3: Add collection-based parallel test**

Add to `src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs`:

```csharp
    [TestMethod]
    public async Task ParallelCollection_ShouldIterateOverItemsAndAggregateOutput()
    {
        // Arrange — workflow: start → setItems → script (MI over items) → end
        var start = new StartEvent("start");
        var setItems = new ScriptTask("setItems",
            "_context.items = new System.Collections.Generic.List<object> { \"A\", \"B\", \"C\" }");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"processed-\" + _context.item"),
            IsSequential: false,
            InputCollection: "items",
            InputDataItem: "item",
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-collection-test",
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

        var instance = await factory.CreateWorkflowInstanceGrain("mi-collection-test");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        // Verify output aggregation
        var rootVars = snapshot.VariableStates.First();
        var resultsDict = (IDictionary<string, object?>)rootVars.Variables;
        Assert.IsTrue(resultsDict.ContainsKey("results"), "Output collection 'results' should exist");
        var results = resultsDict["results"] as IList<object?>;
        Assert.IsNotNull(results, "results should be a list");
        Assert.AreEqual(3, results.Count, "Should have 3 results");
    }

    [TestMethod]
    public async Task SequentialCardinality_ShouldExecuteOneAtATime()
    {
        // Arrange — workflow: start → script (sequential MI x3) → end
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"done-\" + _context.loopCounter"),
            IsSequential: true,
            LoopCardinality: 3);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-sequential-test",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("mi-sequential-test");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.IsTrue(scriptCompletions >= 3, $"Script should have completed at least 3 times, got {scriptCompletions}");
    }

    [TestMethod]
    public async Task ParallelCardinality_ShouldCleanupChildVariableScopes()
    {
        // Arrange
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.iterResult = \"val\""),
            IsSequential: false,
            LoopCardinality: 3);
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-cleanup-test",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var factory = Cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        await factory.DeployWorkflow(workflow, "<xml/>");

        var instance = await factory.CreateWorkflowInstanceGrain("mi-cleanup-test");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        // Child variable scopes should be cleaned up — only root scope should remain
        Assert.AreEqual(1, snapshot.VariableStates.Count,
            "Only root variable scope should remain after multi-instance cleanup");
    }

    [TestMethod]
    public async Task ParallelEmptyCollection_ShouldCompleteImmediately()
    {
        // Arrange — workflow: start → setItems (empty list) → script (MI) → end
        var start = new StartEvent("start");
        var setItems = new ScriptTask("setItems",
            "_context.items = new System.Collections.Generic.List<object>()");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"x\""),
            IsSequential: false,
            InputCollection: "items",
            InputDataItem: "item",
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-empty-test",
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

        var instance = await factory.CreateWorkflowInstanceGrain("mi-empty-test");
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should complete even with empty collection");
    }
```

**Step 4: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/MultiInstanceTests.cs
git commit -m "test: add multi-instance tests for collection, sequential, cleanup, empty collection"
```

---

### Task 7: Update FindScopeForActivity for MI-wrapped SubProcess

When `MultiInstanceActivity` wraps a `SubProcess`, the `FindScopeForActivity` default interface method must recurse into it.

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Definitions/Workflow.cs`

**Step 1: Update FindScopeForActivity**

In `src/Fleans/Fleans.Domain/Definitions/Workflow.cs`, in the `FindScopeForActivity` method (lines 28-41), after the SubProcess recursion block (after line 38 `}`), add:

```csharp

            foreach (var mi in Activities.OfType<MultiInstanceActivity>())
            {
                if (mi.InnerActivity is IWorkflowDefinition innerScope)
                {
                    var result = innerScope.FindScopeForActivity(activityId);
                    if (result is not null)
                        return result;
                }
            }
```

Add `using Fleans.Domain.Activities;` at the top of the file if not already present. The file currently has:
```csharp
using Fleans.Domain.Activities;
```
So it should already be there (line 7).

**Step 2: Build and run tests**

Run: `dotnet build src/Fleans/ && dotnet test src/Fleans/`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Domain/Definitions/Workflow.cs
git commit -m "fix: update FindScopeForActivity to recurse into MI-wrapped SubProcess"
```

---

### Task 8: BPMN parsing for multiInstanceLoopCharacteristics

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs`
- Create: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/MultiInstanceParsingTests.cs`

**Step 1: Write parsing tests**

Create `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/MultiInstanceParsingTests.cs`:

```csharp
using Fleans.Domain.Activities;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class MultiInstanceParsingTests
{
    [TestMethod]
    public async Task Parse_ScriptTask_WithCardinalityMultiInstance()
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

        var miActivity = workflow.Activities.OfType<MultiInstanceActivity>().FirstOrDefault();
        Assert.IsNotNull(miActivity, "Should have a MultiInstanceActivity");
        Assert.AreEqual("script", miActivity.ActivityId);
        Assert.IsFalse(miActivity.IsSequential);
        Assert.AreEqual(5, miActivity.LoopCardinality);
        Assert.IsNull(miActivity.InputCollection);
        Assert.IsInstanceOfType(miActivity.InnerActivity, typeof(ScriptTask));
    }

    [TestMethod]
    public async Task Parse_ScriptTask_WithCollectionMultiInstance()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:zeebe=""http://camunda.org/schema/zeebe/1.0""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""script"" scriptFormat=""csharp"">
      <script>_context.result = _context.item</script>
      <multiInstanceLoopCharacteristics isSequential=""true""
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

        var miActivity = workflow.Activities.OfType<MultiInstanceActivity>().FirstOrDefault();
        Assert.IsNotNull(miActivity, "Should have a MultiInstanceActivity");
        Assert.IsTrue(miActivity.IsSequential);
        Assert.AreEqual("items", miActivity.InputCollection);
        Assert.AreEqual("item", miActivity.InputDataItem);
        Assert.AreEqual("results", miActivity.OutputCollection);
        Assert.AreEqual("result", miActivity.OutputDataItem);
    }

    [TestMethod]
    public async Task Parse_TaskWithoutMultiInstance_ShouldNotWrap()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""script"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""script"" />
    <sequenceFlow id=""f2"" sourceRef=""script"" targetRef=""end"" />
  </process>
</definitions>";

        var converter = new Fleans.Infrastructure.Bpmn.BpmnConverter();
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        Assert.IsFalse(workflow.Activities.Any(a => a is MultiInstanceActivity),
            "Should NOT have a MultiInstanceActivity when no loop characteristics present");
    }
}
```

**Step 2: Add parsing helper to BpmnConverter**

In `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs`, add a new private method before `GetId` (around line 612):

```csharp
    private static MultiInstanceActivity? TryWrapMultiInstance(XElement activityElement, Activity innerActivity)
    {
        var miElement = activityElement.Element(Bpmn + "multiInstanceLoopCharacteristics");
        if (miElement is null) return null;

        var isSequential = bool.TryParse(miElement.Attribute("isSequential")?.Value, out var seq) && seq;

        int? loopCardinality = null;
        var cardinalityEl = miElement.Element(Bpmn + "loopCardinality");
        if (cardinalityEl is not null && int.TryParse(cardinalityEl.Value.Trim(), out var card))
            loopCardinality = card;

        var inputCollection = miElement.Attribute(Zeebe + "collection")?.Value
            ?? miElement.Attribute("collection")?.Value;
        var inputDataItem = miElement.Attribute(Zeebe + "elementVariable")?.Value
            ?? miElement.Attribute("elementVariable")?.Value;
        var outputCollection = miElement.Attribute(Zeebe + "outputCollection")?.Value
            ?? miElement.Attribute("outputCollection")?.Value;
        var outputDataItem = miElement.Attribute(Zeebe + "outputElement")?.Value
            ?? miElement.Attribute("outputElement")?.Value;

        return new MultiInstanceActivity(
            innerActivity.ActivityId,
            innerActivity,
            isSequential,
            loopCardinality,
            inputCollection,
            inputDataItem,
            outputCollection,
            outputDataItem);
    }
```

**Step 3: Apply wrapping after each activity type**

In `ParseActivities`, after each activity creation, check for multi-instance wrapping. We need to apply it to: scriptTask, task, userTask, serviceTask, subProcess, callActivity.

For **scriptTask** (around line 198-200), replace:

```csharp
            var activity = new ScriptTask(id, script, scriptFormat);
            activities.Add(activity);
            activityMap[id] = activity;
```

With:

```csharp
            Activity activity = new ScriptTask(id, script, scriptFormat);
            activity = TryWrapMultiInstance(scriptTask, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
```

For **task** (around line 166-168), replace:

```csharp
            var activity = new TaskActivity(id);
            activities.Add(activity);
            activityMap[id] = activity;
```

With:

```csharp
            Activity activity = new TaskActivity(id);
            activity = TryWrapMultiInstance(task, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
```

For **userTask** (around line 175-177), replace:

```csharp
            var activity = new TaskActivity(id);
            activities.Add(activity);
            activityMap[id] = activity;
```

With:

```csharp
            Activity activity = new TaskActivity(id);
            activity = TryWrapMultiInstance(userTask, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
```

For **serviceTask** (around line 184-186), replace:

```csharp
            var activity = new TaskActivity(id);
            activities.Add(activity);
            activityMap[id] = activity;
```

With:

```csharp
            Activity activity = new TaskActivity(id);
            activity = TryWrapMultiInstance(serviceTask, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
```

For **subProcess** (around line 274-280), replace:

```csharp
            var activity = new SubProcess(id)
            {
                Activities = childActivities,
                SequenceFlows = childSequenceFlows
            };
            activities.Add(activity);
            activityMap[id] = activity;
```

With:

```csharp
            Activity activity = new SubProcess(id)
            {
                Activities = childActivities,
                SequenceFlows = childSequenceFlows
            };
            activity = TryWrapMultiInstance(subProcessEl, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
```

For **callActivity** (around line 314-316), replace:

```csharp
            var activity = new CallActivity(id, calledElement, inputMappings, outputMappings, propagateAllParent, propagateAllChild);
            activities.Add(activity);
            activityMap[id] = activity;
```

With:

```csharp
            Activity activity = new CallActivity(id, calledElement, inputMappings, outputMappings, propagateAllParent, propagateAllChild);
            activity = TryWrapMultiInstance(callActivityEl, activity) ?? activity;
            activities.Add(activity);
            activityMap[id] = activity;
```

**Step 4: Run tests**

Run: `dotnet test src/Fleans/ --filter "MultiInstanceParsingTests"`
Expected: All 3 parsing tests pass

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs \
  src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/MultiInstanceParsingTests.cs
git commit -m "feat: parse multiInstanceLoopCharacteristics and wrap activities"
```

---

### Task 9: Update README and create manual test fixtures

**Files:**
- Modify: `README.md`
- Create: `tests/manual/13-multi-instance/test-plan.md`
- Create: `tests/manual/13-multi-instance/parallel-collection.bpmn`
- Create: `tests/manual/13-multi-instance/parallel-cardinality.bpmn`
- Create: `tests/manual/13-multi-instance/sequential-collection.bpmn`

**Step 1: Update README**

In `README.md`, find the BPMN elements table. After the `| Call Activity |` row, add:

```markdown
| Multi-Instance     | Executes an activity multiple times in parallel or sequentially.            |     [x]     |
```

**Step 2: Create test-plan.md**

Create `tests/manual/13-multi-instance/test-plan.md`:

```markdown
# Multi-Instance Activity

## Scenario 13a: Collection-based parallel

Tests parallel multi-instance over a collection with output aggregation.

### Prerequisites
- None

### Steps
1. Deploy `parallel-collection.bpmn`
2. Start an instance

### Expected
- [ ] Instance status: Completed
- [ ] Completed activities: start, setItems, processItem (3 iterations), end
- [ ] Variables: `results` contains `["processed-A","processed-B","processed-C"]`

---

## Scenario 13b: Cardinality-based parallel

Tests parallel multi-instance with fixed loop count.

### Prerequisites
- None

### Steps
1. Deploy `parallel-cardinality.bpmn`
2. Start an instance

### Expected
- [ ] Instance status: Completed
- [ ] Completed activities: start, repeatTask (3 iterations), end

---

## Scenario 13c: Sequential collection

Tests sequential multi-instance over a collection.

### Prerequisites
- None

### Steps
1. Deploy `sequential-collection.bpmn`
2. Start an instance

### Expected
- [ ] Instance status: Completed
- [ ] Completed activities: start, setItems, processItem (3 iterations), end
- [ ] Variables: `results` contains ordered output
```

**Step 3: Create BPMN fixtures**

Create `tests/manual/13-multi-instance/parallel-collection.bpmn`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
             xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
             xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
             xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
             xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"
             id="Definitions_1" targetNamespace="http://bpmn.io/schema/bpmn">
  <process id="parallel-collection-test" isExecutable="true">
    <startEvent id="start" />
    <scriptTask id="setItems" scriptFormat="csharp">
      <script>_context.items = new System.Collections.Generic.List&lt;object&gt; { "A", "B", "C" }</script>
    </scriptTask>
    <scriptTask id="processItem" scriptFormat="csharp">
      <script>_context.result = "processed-" + _context.item</script>
      <multiInstanceLoopCharacteristics isSequential="false"
        zeebe:collection="items" zeebe:elementVariable="item"
        zeebe:outputCollection="results" zeebe:outputElement="result" />
    </scriptTask>
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="setItems" />
    <sequenceFlow id="f2" sourceRef="setItems" targetRef="processItem" />
    <sequenceFlow id="f3" sourceRef="processItem" targetRef="end" />
  </process>
  <bpmndi:BPMNDiagram id="BPMNDiagram_1">
    <bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="parallel-collection-test">
      <bpmndi:BPMNShape id="start_di" bpmnElement="start"><dc:Bounds x="180" y="200" width="36" height="36" /></bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="setItems_di" bpmnElement="setItems"><dc:Bounds x="270" y="178" width="100" height="80" /></bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="processItem_di" bpmnElement="processItem"><dc:Bounds x="420" y="178" width="100" height="80" /></bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="end_di" bpmnElement="end"><dc:Bounds x="572" y="200" width="36" height="36" /></bpmndi:BPMNShape>
      <bpmndi:BPMNEdge id="f1_di" bpmnElement="f1"><di:waypoint x="216" y="218" /><di:waypoint x="270" y="218" /></bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id="f2_di" bpmnElement="f2"><di:waypoint x="370" y="218" /><di:waypoint x="420" y="218" /></bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id="f3_di" bpmnElement="f3"><di:waypoint x="520" y="218" /><di:waypoint x="572" y="218" /></bpmndi:BPMNEdge>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</definitions>
```

Create `tests/manual/13-multi-instance/parallel-cardinality.bpmn`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
             xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
             xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
             xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
             id="Definitions_1" targetNamespace="http://bpmn.io/schema/bpmn">
  <process id="parallel-cardinality-test" isExecutable="true">
    <startEvent id="start" />
    <scriptTask id="repeatTask" scriptFormat="csharp">
      <script>_context.result = "iter-" + _context.loopCounter</script>
      <multiInstanceLoopCharacteristics isSequential="false">
        <loopCardinality>3</loopCardinality>
      </multiInstanceLoopCharacteristics>
    </scriptTask>
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="repeatTask" />
    <sequenceFlow id="f2" sourceRef="repeatTask" targetRef="end" />
  </process>
  <bpmndi:BPMNDiagram id="BPMNDiagram_1">
    <bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="parallel-cardinality-test">
      <bpmndi:BPMNShape id="start_di" bpmnElement="start"><dc:Bounds x="180" y="200" width="36" height="36" /></bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="repeatTask_di" bpmnElement="repeatTask"><dc:Bounds x="270" y="178" width="100" height="80" /></bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="end_di" bpmnElement="end"><dc:Bounds x="420" y="200" width="36" height="36" /></bpmndi:BPMNShape>
      <bpmndi:BPMNEdge id="f1_di" bpmnElement="f1"><di:waypoint x="216" y="218" /><di:waypoint x="270" y="218" /></bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id="f2_di" bpmnElement="f2"><di:waypoint x="370" y="218" /><di:waypoint x="420" y="218" /></bpmndi:BPMNEdge>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</definitions>
```

Create `tests/manual/13-multi-instance/sequential-collection.bpmn`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
             xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
             xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
             xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
             xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"
             id="Definitions_1" targetNamespace="http://bpmn.io/schema/bpmn">
  <process id="sequential-collection-test" isExecutable="true">
    <startEvent id="start" />
    <scriptTask id="setItems" scriptFormat="csharp">
      <script>_context.items = new System.Collections.Generic.List&lt;object&gt; { "X", "Y", "Z" }</script>
    </scriptTask>
    <scriptTask id="processItem" scriptFormat="csharp">
      <script>_context.result = "seq-" + _context.item</script>
      <multiInstanceLoopCharacteristics isSequential="true"
        zeebe:collection="items" zeebe:elementVariable="item"
        zeebe:outputCollection="results" zeebe:outputElement="result" />
    </scriptTask>
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="setItems" />
    <sequenceFlow id="f2" sourceRef="setItems" targetRef="processItem" />
    <sequenceFlow id="f3" sourceRef="processItem" targetRef="end" />
  </process>
  <bpmndi:BPMNDiagram id="BPMNDiagram_1">
    <bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="sequential-collection-test">
      <bpmndi:BPMNShape id="start_di" bpmnElement="start"><dc:Bounds x="180" y="200" width="36" height="36" /></bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="setItems_di" bpmnElement="setItems"><dc:Bounds x="270" y="178" width="100" height="80" /></bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="processItem_di" bpmnElement="processItem"><dc:Bounds x="420" y="178" width="100" height="80" /></bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="end_di" bpmnElement="end"><dc:Bounds x="572" y="200" width="36" height="36" /></bpmndi:BPMNShape>
      <bpmndi:BPMNEdge id="f1_di" bpmnElement="f1"><di:waypoint x="216" y="218" /><di:waypoint x="270" y="218" /></bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id="f2_di" bpmnElement="f2"><di:waypoint x="370" y="218" /><di:waypoint x="420" y="218" /></bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id="f3_di" bpmnElement="f3"><di:waypoint x="520" y="218" /><di:waypoint x="572" y="218" /></bpmndi:BPMNEdge>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</definitions>
```

**Step 4: Final build and test**

Run: `dotnet build src/Fleans/ && dotnet test src/Fleans/`
Expected: All tests pass

**Step 5: Commit**

```bash
git add README.md tests/manual/13-multi-instance/
git commit -m "docs: add multi-instance to README and create manual test fixtures"
```
