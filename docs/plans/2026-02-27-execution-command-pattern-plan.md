# Execution Command Pattern Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Refactor `Activity.ExecuteAsync()` to return declarative `IExecutionCommand` lists instead of imperatively calling `IWorkflowExecutionContext`, then add a command processor in `WorkflowInstance`.

**Architecture:** Activities return commands describing their intent (spawn activities, register timers/messages/signals, start child workflows, complete). `WorkflowInstance.ProcessCommands()` interprets them uniformly. `IWorkflowExecutionContext` loses 9 mutating methods, keeping only read-only state access.

**Tech Stack:** C# / .NET 10, Orleans, MSTest + NSubstitute

**Design doc:** `docs/plans/2026-02-27-execution-command-pattern-design.md`

---

### Task 1: Create execution command types

**Files:**
- Create: `src/Fleans/Fleans.Domain/ExecutionCommands.cs`

**Step 1: Create the command types file**

```csharp
using Fleans.Domain.Activities;

namespace Fleans.Domain;

public interface IExecutionCommand { }

[GenerateSerializer]
public record CompleteCommand() : IExecutionCommand;

[GenerateSerializer]
public record SpawnActivityCommand(
    [property: Id(0)] Activity Activity,
    [property: Id(1)] Guid? ScopeId,
    [property: Id(2)] Guid? HostActivityInstanceId) : IExecutionCommand;

[GenerateSerializer]
public record OpenSubProcessCommand(
    [property: Id(0)] SubProcess SubProcess,
    [property: Id(1)] Guid ParentVariablesId) : IExecutionCommand;

[GenerateSerializer]
public record RegisterTimerCommand(
    [property: Id(0)] string TimerActivityId,
    [property: Id(1)] TimeSpan DueTime,
    [property: Id(2)] bool IsBoundary) : IExecutionCommand;

[GenerateSerializer]
public record RegisterMessageCommand(
    [property: Id(0)] Guid VariablesId,
    [property: Id(1)] string MessageDefinitionId,
    [property: Id(2)] string ActivityId,
    [property: Id(3)] bool IsBoundary) : IExecutionCommand;

[GenerateSerializer]
public record RegisterSignalCommand(
    [property: Id(0)] string SignalName,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] bool IsBoundary) : IExecutionCommand;

[GenerateSerializer]
public record StartChildWorkflowCommand(
    [property: Id(0)] CallActivity CallActivity) : IExecutionCommand;

[GenerateSerializer]
public record AddConditionsCommand(
    [property: Id(0)] string[] SequenceFlowIds,
    [property: Id(1)] List<ConditionEvaluation> Evaluations) : IExecutionCommand;

[GenerateSerializer]
public record ConditionEvaluation(
    [property: Id(0)] string SequenceFlowId,
    [property: Id(1)] string Condition);

[GenerateSerializer]
public record ThrowSignalCommand(
    [property: Id(0)] string SignalName) : IExecutionCommand;
```

**Step 2: Build to verify no compilation errors**

Run: `dotnet build src/Fleans/Fleans.Domain`
Expected: Build succeeded

**Step 3: Commit**

```
feat: add execution command types for declarative activity execution
```

---

### Task 2: Change Activity.ExecuteAsync signature and update all activities

This task changes the `ExecuteAsync` return type from `Task` to `Task<IReadOnlyList<IExecutionCommand>>` across the base class and all 17 activity subclasses. The codebase will not compile until all activities are updated, so this must be done atomically.

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/Activity.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/StartEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/EndEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/TaskActivity.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/ScriptTask.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/SubProcess.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/CallActivity.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/ExclusiveGateway.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/ConditionalGateway.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/ParallelGateway.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/EventBasedGateway.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/TimerIntermediateCatchEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/MessageIntermediateCatchEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/SignalIntermediateCatchEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/SignalIntermediateThrowEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/BoundaryTimerEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/BoundaryErrorEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/MessageBoundaryEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/SignalBoundaryEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/TimerStartEvent.cs`

**Step 1: Update Activity base class**

Change `Activity.cs` — the base `ExecuteAsync` now returns commands. It still calls `activityContext.Execute()` and publishes the domain event, but returns an empty command list (subclasses append their own):

```csharp
using System.Runtime.CompilerServices;
using Fleans.Domain.Events;
using Orleans;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record Activity([property: Id(0)] string ActivityId)
{
    internal virtual bool IsJoinGateway => false;

    internal virtual async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        await activityContext.Execute();
        await activityContext.PublishEvent(new WorkflowActivityExecutedEvent(
            await workflowContext.GetWorkflowInstanceId(),
            definition.WorkflowId,
            await activityContext.GetActivityInstanceId(),
            ActivityId,
            GetType().Name));
        return [];
    }

    internal abstract Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition);
}
```

**Step 2: Update BoundarableActivity**

Replace `RegisterBoundaryEventsAsync` with `BuildBoundaryRegistrationCommands` that returns commands instead of calling workflowContext:

```csharp
using Orleans;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record BoundarableActivity(string ActivityId)
    : Activity(ActivityId)
{
    internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
        commands.AddRange(await BuildBoundaryRegistrationCommands(activityContext, definition));
        return commands;
    }

    private async Task<List<IExecutionCommand>> BuildBoundaryRegistrationCommands(
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = new List<IExecutionCommand>();

        foreach (var boundaryTimer in definition.Activities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == ActivityId))
        {
            commands.Add(new RegisterTimerCommand(boundaryTimer.ActivityId,
                boundaryTimer.TimerDefinition.GetDueTime(), IsBoundary: true));
        }

        var variablesId = await activityContext.GetVariablesStateId();
        foreach (var boundaryMsg in definition.Activities.OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == ActivityId))
        {
            commands.Add(new RegisterMessageCommand(variablesId, boundaryMsg.MessageDefinitionId,
                boundaryMsg.ActivityId, IsBoundary: true));
        }

        foreach (var boundarySignal in definition.Activities.OfType<SignalBoundaryEvent>()
            .Where(bs => bs.AttachedToActivityId == ActivityId))
        {
            var signalDef = definition.Signals.First(s => s.Id == boundarySignal.SignalDefinitionId);
            commands.Add(new RegisterSignalCommand(signalDef.Name,
                boundarySignal.ActivityId, IsBoundary: true));
        }

        return commands;
    }
}
```

**Step 3: Update simple activities that just complete**

**StartEvent.cs** — returns `[CompleteCommand]`:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    commands.Add(new CompleteCommand());
    return commands;
}
```

**EndEvent.cs** — returns `[CompleteCommand]` (workflow completion stays via `workflowContext.Complete()` since it's a read on "are we at root scope?" + a state change the processor will handle):

Note: EndEvent is special — it completes the workflow if at root scope. This should remain in the activity since it's conditional logic, not infrastructure. Keep `workflowContext.Complete()` call.

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    commands.Add(new CompleteCommand());

    if (definition.IsRootScope)
        await workflowContext.Complete();

    return commands;
}
```

**TaskActivity.cs** — only has `GetNextActivities`, no `ExecuteAsync` override. It inherits from `BoundarableActivity`, which already returns boundary commands. But TaskActivity doesn't complete in Execute (it's completed externally via `CompleteActivity`). No `ExecuteAsync` override needed — base BoundarableActivity behavior is correct.

Check: TaskActivity currently has NO `ExecuteAsync` override. The base BoundarableActivity calls `activityContext.Execute()` + publishes event + builds boundary commands. TaskActivity is completed externally via `CompleteActivity(activityId, variables)`. So TaskActivity's `ExecuteAsync` stays inherited. No change needed.

**BoundaryTimerEvent.cs** — returns `[CompleteCommand]`:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    commands.Add(new CompleteCommand());
    return commands;
}
```

Apply same pattern to **BoundaryErrorEvent.cs**, **MessageBoundaryEvent.cs**, **SignalBoundaryEvent.cs**.

**EventBasedGateway.cs** — returns `[CompleteCommand]`:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    commands.Add(new CompleteCommand());
    return commands;
}
```

**TimerStartEvent.cs** — check its current implementation and apply same pattern as StartEvent if it just completes.

**Step 4: Update ScriptTask**

ScriptTask publishes `ExecuteScriptEvent` and does NOT complete (script executor completes it externally). It inherits from TaskActivity → BoundarableActivity. The event publishing stays via `activityContext.PublishEvent()`:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();

    await activityContext.PublishEvent(new ExecuteScriptEvent(
        await workflowContext.GetWorkflowInstanceId(),
        definition.WorkflowId,
        definition.ProcessDefinitionId,
        await activityContext.GetActivityInstanceId(),
        ActivityId,
        Script,
        ScriptFormat));

    return commands;
}
```

**Step 5: Update gateways**

**ParallelGateway.cs** — fork returns `[CompleteCommand]`, join returns `[CompleteCommand]` if all paths done or `[]`:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();

    if (IsFork)
    {
        commands.Add(new CompleteCommand());
    }
    else
    {
        if (await AllIncomingPathsCompleted(workflowContext, definition))
        {
            commands.Add(new CompleteCommand());
        }
    }

    return commands;
}
```

**ExclusiveGateway.cs** — returns `[AddConditionsCommand]` or `[CompleteCommand]` if no conditions:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();

    var activityId = await activityContext.GetActivityId();
    var sequences = definition.SequenceFlows.OfType<ConditionalSequenceFlow>()
        .Where(sf => sf.Source.ActivityId == activityId)
        .ToArray();

    if (sequences.Length == 0)
    {
        commands.Add(new CompleteCommand());
        return commands;
    }

    var sequenceFlowIds = sequences.Select(s => s.SequenceFlowId).ToArray();
    var evaluations = sequences.Select(s => new ConditionEvaluation(s.SequenceFlowId, s.Condition)).ToList();
    commands.Add(new AddConditionsCommand(sequenceFlowIds, evaluations));

    return commands;
}
```

Note: `AddConditionalSequencesToWorkflowInstance` and `QueueEvaluateConditionEvents` private methods are no longer needed — remove them.

**ConditionalGateway.cs** — check if it has logic that needs updating. It's an abstract class between Gateway and ExclusiveGateway. Read it to see what it does.

**Step 6: Update catch events**

**TimerIntermediateCatchEvent.cs** — returns `[RegisterTimerCommand]` (no complete):

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    commands.Add(new RegisterTimerCommand(ActivityId, TimerDefinition.GetDueTime(), IsBoundary: false));
    return commands;
}
```

**MessageIntermediateCatchEvent.cs** — returns `[RegisterMessageCommand]`:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    var variablesId = await activityContext.GetVariablesStateId();
    commands.Add(new RegisterMessageCommand(variablesId, MessageDefinitionId, ActivityId, IsBoundary: false));
    return commands;
}
```

**SignalIntermediateCatchEvent.cs** — returns `[RegisterSignalCommand]`:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    var signalDef = definition.Signals.First(s => s.Id == SignalDefinitionId);
    commands.Add(new RegisterSignalCommand(signalDef.Name, ActivityId, IsBoundary: false));
    return commands;
}
```

**Step 7: Update SubProcess and CallActivity**

**SubProcess.cs** — returns `[OpenSubProcessCommand]`:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    var variablesId = await activityContext.GetVariablesStateId();
    commands.Add(new OpenSubProcessCommand(this, variablesId));
    return commands;
}
```

**CallActivity.cs** — returns `[StartChildWorkflowCommand]`:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    commands.Add(new StartChildWorkflowCommand(this));
    return commands;
}
```

**Step 8: Update SignalIntermediateThrowEvent**

Returns `[ThrowSignalCommand, CompleteCommand]`:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    var signalDef = definition.Signals.First(s => s.Id == SignalDefinitionId);
    commands.Add(new ThrowSignalCommand(signalDef.Name));
    commands.Add(new CompleteCommand());
    return commands;
}
```

**Step 9: Build Fleans.Domain to verify all activities compile**

Run: `dotnet build src/Fleans/Fleans.Domain`
Expected: Build succeeded (tests and Application will still fail — that's Task 3+)

**Step 10: Commit**

```
refactor: change Activity.ExecuteAsync to return execution commands

All 17 activity subclasses now return IReadOnlyList<IExecutionCommand>
instead of imperatively calling IWorkflowExecutionContext. BoundarableActivity
builds boundary registration commands declaratively.
```

---

### Task 3: Add ProcessCommands to WorkflowInstance and update ExecuteWorkflow

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`

**Step 1: Add ProcessCommands method**

Add after the `ExecuteWorkflow` method (~line 79):

```csharp
private async Task ProcessCommands(
    IReadOnlyList<IExecutionCommand> commands,
    ActivityInstanceEntry entry,
    IActivityExecutionContext activityContext)
{
    foreach (var command in commands)
    {
        switch (command)
        {
            case CompleteCommand:
                await activityContext.Complete();
                break;

            case SpawnActivityCommand spawn:
                // Generic spawn — used for future multi-instance
                var spawnId = Guid.NewGuid();
                var spawnInstance = _grainFactory.GetGrain<IActivityInstanceGrain>(spawnId);
                await spawnInstance.SetActivity(spawn.Activity.ActivityId, spawn.Activity.GetType().Name);
                var spawnVarsId = await activityContext.GetVariablesStateId();
                await spawnInstance.SetVariablesId(spawnVarsId);
                var spawnEntry = new ActivityInstanceEntry(spawnId, spawn.Activity.ActivityId, State.Id, spawn.ScopeId);
                State.AddEntries([spawnEntry]);
                break;

            case OpenSubProcessCommand sub:
                await OpenSubProcessScope(entry.ActivityInstanceId, sub.SubProcess, sub.ParentVariablesId);
                break;

            case RegisterTimerCommand timer:
                await RegisterTimerReminder(entry.ActivityInstanceId, timer.TimerActivityId, timer.DueTime);
                break;

            case RegisterMessageCommand msg:
                if (msg.IsBoundary)
                    await RegisterBoundaryMessageSubscription(msg.VariablesId,
                        entry.ActivityInstanceId, msg.ActivityId, msg.MessageDefinitionId);
                else
                    await RegisterMessageSubscription(msg.VariablesId, msg.MessageDefinitionId, msg.ActivityId);
                break;

            case RegisterSignalCommand sig:
                if (sig.IsBoundary)
                    await RegisterBoundarySignalSubscription(
                        entry.ActivityInstanceId, sig.ActivityId, sig.SignalName);
                else
                    await RegisterSignalSubscription(sig.SignalName, sig.ActivityId,
                        entry.ActivityInstanceId);
                break;

            case StartChildWorkflowCommand child:
                await StartChildWorkflow(child.CallActivity, activityContext);
                break;

            case AddConditionsCommand cond:
                await AddConditionSequenceStates(entry.ActivityInstanceId, cond.SequenceFlowIds);
                foreach (var eval in cond.Evaluations)
                {
                    await activityContext.PublishEvent(new Domain.Events.EvaluateConditionEvent(
                        await GetWorkflowInstanceId(),
                        (await GetWorkflowDefinition()).WorkflowId,
                        (await GetWorkflowDefinition()).ProcessDefinitionId,
                        entry.ActivityInstanceId,
                        entry.ActivityId,
                        eval.SequenceFlowId,
                        eval.Condition));
                }
                break;

            case ThrowSignalCommand sig:
                await ThrowSignal(sig.SignalName);
                break;
        }
    }
}
```

**Step 2: Update ExecuteWorkflow to process commands**

Change `ExecuteWorkflow()` at line 72 from:

```csharp
await currentActivity.ExecuteAsync(this, activityState, scopeDefinition);
```

to:

```csharp
var commands = await currentActivity.ExecuteAsync(this, activityState, scopeDefinition);
var currentEntry = State.GetActiveActivities()
    .First(e => e.ActivityId == activityId && !e.IsCompleted);
await ProcessCommands(commands, currentEntry, activityState);
```

**Step 3: Build to verify**

Run: `dotnet build src/Fleans/Fleans.Application`
Expected: Build succeeded (tests may still fail)

**Step 4: Commit**

```
feat: add ProcessCommands to WorkflowInstance for declarative execution
```

---

### Task 4: Slim IWorkflowExecutionContext

**Files:**
- Modify: `src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs` — methods stay as private/internal, just remove from interface

**Step 1: Remove 9 imperative methods from IWorkflowExecutionContext**

The interface becomes:

```csharp
using Fleans.Domain.States;

namespace Fleans.Domain;

public interface IWorkflowExecutionContext
{
    ValueTask<Guid> GetWorkflowInstanceId();
    ValueTask Complete();

    ValueTask<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
    ValueTask SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result);

    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();
    ValueTask<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();

    ValueTask<object?> GetVariable(Guid variablesId, string variableName);
}
```

Removed:
- `StartChildWorkflow` — now `StartChildWorkflowCommand`
- `OpenSubProcessScope` — now `OpenSubProcessCommand`
- `RegisterTimerReminder` — now `RegisterTimerCommand`
- `RegisterMessageSubscription` — now `RegisterMessageCommand`
- `RegisterBoundaryMessageSubscription` — now `RegisterMessageCommand(IsBoundary: true)`
- `RegisterSignalSubscription` — now `RegisterSignalCommand`
- `RegisterBoundarySignalSubscription` — now `RegisterSignalCommand(IsBoundary: true)`
- `ThrowSignal` — now `ThrowSignalCommand`
- `AddConditionSequenceStates` — now `AddConditionsCommand`

**Step 2: Update WorkflowInstance**

Remove the explicit interface implementations for the 9 removed methods. Keep the methods themselves as private/internal since `ProcessCommands()` and other internal code calls them. Specifically:

- Change `public async ValueTask OpenSubProcessScope(...)` to `private async Task OpenSubProcessScope(...)`
- Change `public async ValueTask StartChildWorkflow(...)` to `private async Task StartChildWorkflow(...)`
- Keep `RegisterTimerReminder`, `RegisterMessageSubscription`, etc. as private methods (they may already be private or called from `ProcessCommands`)
- `AddConditionSequenceStates` and `ThrowSignal` — make private

Also remove these from `IBoundaryEventStateAccessor` if they're exposed there. Check the accessor interface.

**Step 3: Update IBoundaryEventStateAccessor if needed**

The `IBoundaryEventStateAccessor` at the top of `WorkflowInstance.cs` delegates some methods. Check if any removed methods are exposed through it and update accordingly. `OpenSubProcessScope` is NOT on this accessor. `RegisterTimerReminder` etc. are NOT on this accessor. So likely no changes needed here.

**Step 4: Build to verify**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded (or fix any compilation errors from method visibility changes)

**Step 5: Commit**

```
refactor: remove 9 imperative methods from IWorkflowExecutionContext
```

---

### Task 5: Update test infrastructure and all domain tests

**Files:**
- Modify: `src/Fleans/Fleans.Domain.Tests/ActivityTestHelper.cs`
- Modify: All 27 test files in `src/Fleans/Fleans.Domain.Tests/`

**Step 1: Update ActivityTestHelper.CreateWorkflowContext**

Remove the mock setups for the 9 removed methods. The updated helper:

```csharp
public static IWorkflowExecutionContext CreateWorkflowContext(IWorkflowDefinition definition)
{
    var context = Substitute.For<IWorkflowExecutionContext>();
    context.GetWorkflowInstanceId().Returns(ValueTask.FromResult(Guid.NewGuid()));
    context.GetConditionSequenceStates()
        .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(
            new Dictionary<Guid, ConditionSequenceState[]>()));
    context.GetActiveActivities()
        .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>([]));
    context.GetCompletedActivities()
        .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>([]));
    context.GetVariable(Arg.Any<Guid>(), Arg.Any<string>())
        .Returns(ValueTask.FromResult<object?>(null));
    return context;
}
```

Removed stubs for: `StartChildWorkflow`, `OpenSubProcessScope`, `RegisterMessageSubscription`, `RegisterBoundaryMessageSubscription`, `RegisterTimerReminder`.

**Step 2: Update ExecuteAsync test assertions**

All tests that call `activity.ExecuteAsync(...)` now get back a command list. Update tests to:

1. Capture the returned commands: `var commands = await activity.ExecuteAsync(workflowContext, activityContext, definition);`
2. Assert on the command list instead of `Received()` calls on workflowContext

Example — **StartEventActivityTests.cs** `ExecuteAsync_ShouldCallComplete_OnActivityContext`:

```csharp
[TestMethod]
public async Task ExecuteAsync_ShouldReturnCompleteCommand()
{
    // Arrange
    var startEvent = new StartEvent("start");
    var end = new EndEvent("end");
    var definition = ActivityTestHelper.CreateWorkflowDefinition(
        [startEvent, end],
        [new SequenceFlow("seq1", startEvent, end)]);
    var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
    var (activityContext, _) = ActivityTestHelper.CreateActivityContext("start");

    // Act
    var commands = await startEvent.ExecuteAsync(workflowContext, activityContext, definition);

    // Assert
    await activityContext.Received(1).Execute();
    Assert.IsTrue(commands.Any(c => c is CompleteCommand));
}
```

Example — **BoundarableActivityTests.cs** `ExecuteAsync_ShouldRegisterTimerReminder_WhenBoundaryTimerAttached`:

```csharp
[TestMethod]
public async Task ExecuteAsync_ShouldReturnRegisterTimerCommand_WhenBoundaryTimerAttached()
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
    var commands = await task.ExecuteAsync(workflowContext, activityContext, definition);

    // Assert
    var timerCmd = commands.OfType<RegisterTimerCommand>().Single();
    Assert.AreEqual("bt1", timerCmd.TimerActivityId);
    Assert.AreEqual(TimeSpan.FromMinutes(10), timerCmd.DueTime);
    Assert.IsTrue(timerCmd.IsBoundary);
}
```

**Step 3: Update ALL test files following the same pattern**

For each test file, change `Received()` assertions on workflowContext imperative methods to command list assertions:

- `workflowContext.Received(1).RegisterTimerReminder(...)` → `commands.OfType<RegisterTimerCommand>().Single()` with property assertions
- `workflowContext.Received(1).RegisterBoundaryMessageSubscription(...)` → `commands.OfType<RegisterMessageCommand>().Single(c => c.IsBoundary)`
- `workflowContext.Received(1).StartChildWorkflow(...)` → `commands.OfType<StartChildWorkflowCommand>().Single()`
- `workflowContext.Received(1).OpenSubProcessScope(...)` → `commands.OfType<OpenSubProcessCommand>().Single()`
- `activityContext.Received(1).Complete()` → `commands.Any(c => c is CompleteCommand)`
- `workflowContext.DidNotReceive().RegisterTimerReminder(...)` → `Assert.IsFalse(commands.OfType<RegisterTimerCommand>().Any())`

Test files to update (check each for `ExecuteAsync` test methods):
- `StartEventActivityTests.cs`
- `EndEventActivityTests.cs`
- `TaskActivityDomainTests.cs`
- `ScriptTaskActivityTests.cs`
- `BoundarableActivityTests.cs`
- `BoundaryTimerEventDomainTests.cs`
- `BoundaryErrorEventDomainTests.cs`
- `MessageBoundaryEventDomainTests.cs`
- `CallActivityDomainTests.cs`
- `SubProcessActivityTests.cs`
- `ExclusiveGatewayActivityTests.cs`
- `ConditionalGatewayActivityTests.cs`
- `ParallelGatewayActivityTests.cs`
- `EventBasedGatewayActivityTests.cs`
- `TimerIntermediateCatchEventDomainTests.cs`
- `MessageIntermediateCatchEventDomainTests.cs`
- `TimerStartEventDomainTests.cs`
- `EndEventScopeTests.cs`

Note: `GetNextActivities` tests do NOT change — that method's signature is unchanged.

**Step 4: Build tests**

Run: `dotnet build src/Fleans/Fleans.Domain.Tests`
Expected: Build succeeded

**Step 5: Commit**

```
test: update domain tests for execution command pattern
```

---

### Task 6: Build and run full test suite

**Step 1: Build entire solution**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded, 0 errors

Fix any remaining compilation errors discovered during the full build. Common issues:
- Integration tests in other test projects that call `ExecuteAsync`
- Any code that uses the removed `IWorkflowExecutionContext` methods

**Step 2: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 3: Fix any failing tests**

If tests fail, investigate root cause. Common issues:
- Commands not being returned in the right order
- Missing `CompleteCommand` in activities that used to call `activityContext.Complete()` directly
- `ProcessCommands` not handling a case correctly

**Step 4: Final commit**

```
chore: verify all tests pass after execution command pattern refactor
```

---

### Summary

| Task | Description | Files |
|------|-------------|-------|
| 1 | Create command types | 1 new file |
| 2 | Change Activity.ExecuteAsync + update all activities | ~20 activity files |
| 3 | Add ProcessCommands to WorkflowInstance | 1 file |
| 4 | Slim IWorkflowExecutionContext | 2 files |
| 5 | Update test infrastructure + all tests | ~20 test files |
| 6 | Build + run full test suite | 0 files (verification) |
