# Pass IWorkflowDefinition as Parameter — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Remove `GetWorkflowDefinition()` from `IWorkflowExecutionContext` and `IBoundaryEventStateAccessor`, pass `IWorkflowDefinition` as a parameter to activity methods instead.

**Architecture:** Activities receive the definition as a parameter from `WorkflowInstance`, which resolves it once from `IProcessDefinitionGrain` and passes it down. This eliminates redundant async hops and shrinks the `IWorkflowExecutionContext` surface. `IWorkflowInstanceGrain.GetWorkflowDefinition()` remains for grain-to-grain calls (e.g. `MessageCorrelationGrain`).

**Tech Stack:** C# / .NET / Orleans / MSTest / NSubstitute

---

### Task 1: Update domain interfaces and base classes

**Files:**
- Modify: `src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/Activity.cs`
- Modify: `src/Fleans/Fleans.Domain/IBoundarableActivity.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs`

**Step 1: Remove `GetWorkflowDefinition()` from `IWorkflowExecutionContext`**

```csharp
// IWorkflowExecutionContext.cs — remove line 8
// BEFORE:
    ValueTask<IWorkflowDefinition> GetWorkflowDefinition();
// AFTER: (line deleted)
```

**Step 2: Add `IWorkflowDefinition definition` parameter to `Activity` base class**

```csharp
// Activity.cs
internal virtual async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    await activityContext.Execute();
    await activityContext.PublishEvent(new WorkflowActivityExecutedEvent(await workflowContext.GetWorkflowInstanceId(),
        definition.WorkflowId,
        await activityContext.GetActivityInstanceId(),
        ActivityId,
        GetType().Name));
}

internal abstract Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition);
```

Note: `Activity.ExecuteAsync` already fetched the definition just to get `WorkflowId`. Now it uses the passed `definition` directly and the `var definition = await workflowContext.GetWorkflowDefinition();` line is removed.

**Step 3: Add `IWorkflowDefinition definition` parameter to `IBoundarableActivity`**

```csharp
// IBoundarableActivity.cs
public interface IBoundarableActivity
{
    Task RegisterBoundaryEventsAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition);
}
```

**Step 4: Update `BoundarableActivity` to use passed definition**

```csharp
// BoundarableActivity.cs
public async Task RegisterBoundaryEventsAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
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
```

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs \
        src/Fleans/Fleans.Domain/Activities/Activity.cs \
        src/Fleans/Fleans.Domain/IBoundarableActivity.cs \
        src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs
git commit -m "refactor: add IWorkflowDefinition parameter to Activity base and remove from IWorkflowExecutionContext"
```

---

### Task 2: Update all domain activity implementations

**Files (all in `src/Fleans/Fleans.Domain/Activities/`):**
- Modify: `StartEvent.cs`
- Modify: `EndEvent.cs`
- Modify: `TaskActivity.cs`
- Modify: `ScriptTask.cs`
- Modify: `CallActivity.cs`
- Modify: `ErrorEvent.cs`
- Modify: `BoundaryErrorEvent.cs`
- Modify: `BoundaryTimerEvent.cs`
- Modify: `MessageBoundaryEvent.cs`
- Modify: `TimerStartEvent.cs`
- Modify: `TimerIntermediateCatchEvent.cs`
- Modify: `MessageIntermediateCatchEvent.cs`
- Modify: `ExclusiveGateway.cs`
- Modify: `ConditionalGateway.cs`
- Modify: `ParallelGateway.cs`

Each activity override gets the `IWorkflowDefinition definition` parameter added. Inside each method, replace `await workflowContext.GetWorkflowDefinition()` with direct use of `definition`.

**Step 1: Update StartEvent**

```csharp
// StartEvent.cs
internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);
    await activityContext.Complete();
}

internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
    return Task.FromResult(nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>());
}
```

Note: `GetNextActivities` no longer needs `async` since it no longer awaits anything.

**Step 2: Update EndEvent**

```csharp
// EndEvent.cs
internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);
    await activityContext.Complete();
    await workflowContext.Complete();
}

internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    return Task.FromResult(new List<Activity>());
}
```

**Step 3: Update TaskActivity**

```csharp
// TaskActivity.cs
internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
    return Task.FromResult(nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>());
}
```

**Step 4: Update ScriptTask**

```csharp
// ScriptTask.cs
internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);

    await activityContext.PublishEvent(new ExecuteScriptEvent(
        await workflowContext.GetWorkflowInstanceId(),
        definition.WorkflowId,
        definition.ProcessDefinitionId,
        await activityContext.GetActivityInstanceId(),
        ActivityId,
        Script,
        ScriptFormat));
}
```

**Step 5: Update CallActivity**

```csharp
// CallActivity.cs
internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);
    await workflowContext.StartChildWorkflow(this, activityContext);
}

internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
    return Task.FromResult(nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>());
}
```

**Step 6: Update ErrorEvent**

```csharp
// ErrorEvent.cs
internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);
    await activityContext.Complete();
}

internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
    return Task.FromResult(nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>());
}
```

**Step 7: Update BoundaryErrorEvent**

```csharp
// BoundaryErrorEvent.cs
internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);
    await activityContext.Complete();
}

internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
    return Task.FromResult(nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>());
}
```

**Step 8: Update BoundaryTimerEvent**

```csharp
// BoundaryTimerEvent.cs
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
    var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
    return Task.FromResult(nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>());
}
```

**Step 9: Update MessageBoundaryEvent**

```csharp
// MessageBoundaryEvent.cs
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
    var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
    return Task.FromResult(nextFlow != null ? [nextFlow.Target] : []);
}
```

**Step 10: Update TimerStartEvent**

```csharp
// TimerStartEvent.cs
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
    var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
    return Task.FromResult(nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>());
}
```

**Step 11: Update TimerIntermediateCatchEvent**

```csharp
// TimerIntermediateCatchEvent.cs
internal override async Task ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);
    var hostInstanceId = await activityContext.GetActivityInstanceId();
    await workflowContext.RegisterTimerReminder(hostInstanceId, ActivityId, TimerDefinition.GetDueTime());
    // Do NOT call activityContext.Complete() — the reminder will do that
}

internal override Task<List<Activity>> GetNextActivities(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
    return Task.FromResult(nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>());
}
```

**Step 12: Update MessageIntermediateCatchEvent**

```csharp
// MessageIntermediateCatchEvent.cs
internal override async Task ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);
    await workflowContext.RegisterMessageSubscription(MessageDefinitionId, ActivityId);
    // Do NOT call activityContext.Complete() — the correlation grain will do that
}

internal override Task<List<Activity>> GetNextActivities(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
    return Task.FromResult(nextFlow != null ? [nextFlow.Target] : []);
}
```

**Step 13: Update ExclusiveGateway**

```csharp
// ExclusiveGateway.cs
internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);

    var sequences = await AddConditionalSequencesToWorkflowInstance(workflowContext, activityContext, definition);

    if (!sequences.Any())
    {
        await activityContext.Complete();
        return;
    }

    await QueueEvaluateConditionEvents(workflowContext, activityContext, definition, sequences);
}

private static async Task<IEnumerable<ConditionalSequenceFlow>> AddConditionalSequencesToWorkflowInstance(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    var activityId = await activityContext.GetActivityId();

    var sequences = definition.SequenceFlows.OfType<ConditionalSequenceFlow>()
                            .Where(sf => sf.Source.ActivityId == activityId)
                            .ToArray();

    var sequenceFlowIds = sequences.Select(s => s.SequenceFlowId).ToArray();
    await workflowContext.AddConditionSequenceStates(await activityContext.GetActivityInstanceId(), sequenceFlowIds);
    return sequences;
}

private async Task QueueEvaluateConditionEvents(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition, IEnumerable<ConditionalSequenceFlow> sequences)
{
    foreach (var sequence in sequences)
    {
        await activityContext.PublishEvent(new EvaluateConditionEvent(await workflowContext.GetWorkflowInstanceId(),
                                                           definition.WorkflowId,
                                                            definition.ProcessDefinitionId,
                                                            await activityContext.GetActivityInstanceId(),
                                                            ActivityId,
                                                            sequence.SequenceFlowId,
                                                            sequence.Condition));
    }
}

internal override async Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    var sequencesState = await workflowContext.GetConditionSequenceStates();
    var activityInstanceId = await activityContext.GetActivityInstanceId();
    if (!sequencesState.TryGetValue(activityInstanceId, out var activitySequencesState))
        activitySequencesState = [];

    var trueTarget = activitySequencesState
        .FirstOrDefault(x => x.Result);

    if (trueTarget is not null)
    {
        var flow = definition.SequenceFlows
            .FirstOrDefault(sf => sf.SequenceFlowId == trueTarget.ConditionalSequenceFlowId)
            ?? throw new InvalidOperationException(
                $"Sequence flow '{trueTarget.ConditionalSequenceFlowId}' not found in workflow definition");
        return [flow.Target];
    }

    var defaultFlow = definition.SequenceFlows
        .OfType<DefaultSequenceFlow>()
        .FirstOrDefault(sf => sf.Source.ActivityId == ActivityId);

    if (defaultFlow is not null)
        return [defaultFlow.Target];

    throw new InvalidOperationException(
        $"ExclusiveGateway {ActivityId}: no true condition and no default flow");
}
```

**Step 14: Update ConditionalGateway**

Add `IWorkflowDefinition definition` parameter to `SetConditionResult`:

```csharp
// ConditionalGateway.cs
internal async Task<bool> SetConditionResult(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    string conditionSequenceFlowId,
    bool result,
    IWorkflowDefinition definition)
{
    var activityInstanceId = await activityContext.GetActivityInstanceId();
    await workflowContext.SetConditionSequenceResult(activityInstanceId, conditionSequenceFlowId, result);

    if (result)
        return true;

    var sequences = await workflowContext.GetConditionSequenceStates();
    if (!sequences.TryGetValue(activityInstanceId, out var mySequences))
        return false;

    if (mySequences.All(s => s.IsEvaluated))
    {
        var hasDefault = definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .Any(sf => sf.Source.ActivityId == ActivityId);

        if (!hasDefault)
            throw new InvalidOperationException(
                $"Gateway {ActivityId}: all conditions evaluated to false and no default flow exists");

        return true;
    }

    return false;
}
```

**Step 15: Update ParallelGateway**

```csharp
// ParallelGateway.cs
internal override async Task ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    await base.ExecuteAsync(workflowContext, activityContext, definition);

    if (IsFork)
    {
        await activityContext.Complete();
    }
    else
    {
        if (await AllIncomingPathsCompleted(workflowContext, definition))
        {
            await activityContext.Complete();
        }
        else
        {
            await activityContext.Execute();
        }
    }
}

internal override Task<List<Activity>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
{
    var nextFlows = definition.SequenceFlows.Where(sf => sf.Source == this)
        .Select(flow => flow.Target)
        .ToList();

    return Task.FromResult(nextFlows);
}
```

Note: `AllIncomingPathsCompleted` already takes `IWorkflowDefinition` — no change needed for that private method.

**Step 16: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/
git commit -m "refactor: update all domain activities to accept IWorkflowDefinition parameter"
```

---

### Task 3: Update application-layer interfaces

**Files:**
- Modify: `src/Fleans/Fleans.Application/Services/IBoundaryEventStateAccessor.cs`
- Modify: `src/Fleans/Fleans.Application/Services/IBoundaryEventHandler.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs`

**Step 1: Remove `GetWorkflowDefinition()` from `IBoundaryEventStateAccessor`**

```csharp
// IBoundaryEventStateAccessor.cs — full file
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Fleans.Application.Services;

public interface IBoundaryEventStateAccessor
{
    WorkflowInstanceState State { get; }
    IGrainFactory GrainFactory { get; }
    ILogger Logger { get; }
    IWorkflowExecutionContext WorkflowExecutionContext { get; }
    ValueTask<object?> GetVariable(string variableName);
    Task TransitionToNextActivity();
    Task ExecuteWorkflow();
}
```

**Step 2: Add `IWorkflowDefinition definition` to `IBoundaryEventHandler` methods**

```csharp
// IBoundaryEventHandler.cs — full file
using Fleans.Domain;
using Fleans.Domain.Activities;

namespace Fleans.Application.Services;

public interface IBoundaryEventHandler
{
    void Initialize(IBoundaryEventStateAccessor accessor);
    Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId);
    Task HandleBoundaryMessageFiredAsync(MessageBoundaryEvent boundaryMessage, Guid hostActivityInstanceId, IWorkflowDefinition definition);
    Task HandleBoundaryErrorAsync(string activityId, BoundaryErrorEvent boundaryError, Guid activityInstanceId, IWorkflowDefinition definition);
    Task UnregisterBoundaryTimerRemindersAsync(string activityId, Guid hostActivityInstanceId, IWorkflowDefinition definition);
    Task UnsubscribeBoundaryMessageSubscriptionsAsync(string activityId, IWorkflowDefinition definition, string? skipMessageName = null);
}
```

Note: `HandleBoundaryTimerFiredAsync` does NOT need the definition — it doesn't use it (only calls `UnsubscribeBoundaryMessageSubscriptionsAsync` which does). However, it calls `UnsubscribeBoundaryMessageSubscriptionsAsync` internally, so it does need it. Let me check...

Actually looking at `BoundaryEventHandler.HandleBoundaryTimerFiredAsync` — it calls `UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId)` and `CreateAndExecuteBoundaryInstanceAsync`. The `CreateAndExecuteBoundaryInstanceAsync` calls `boundaryActivity.ExecuteAsync` which now needs definition. So yes, `HandleBoundaryTimerFiredAsync` also needs the definition:

```csharp
// IBoundaryEventHandler.cs — corrected full file
using Fleans.Domain;
using Fleans.Domain.Activities;

namespace Fleans.Application.Services;

public interface IBoundaryEventHandler
{
    void Initialize(IBoundaryEventStateAccessor accessor);
    Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId, IWorkflowDefinition definition);
    Task HandleBoundaryMessageFiredAsync(MessageBoundaryEvent boundaryMessage, Guid hostActivityInstanceId, IWorkflowDefinition definition);
    Task HandleBoundaryErrorAsync(string activityId, BoundaryErrorEvent boundaryError, Guid activityInstanceId, IWorkflowDefinition definition);
    Task UnregisterBoundaryTimerRemindersAsync(string activityId, Guid hostActivityInstanceId, IWorkflowDefinition definition);
    Task UnsubscribeBoundaryMessageSubscriptionsAsync(string activityId, IWorkflowDefinition definition, string? skipMessageName = null);
}
```

**Step 3: Update `IWorkflowInstanceGrain`**

Remove the `new` keyword from `GetWorkflowDefinition()` since it's no longer on `IWorkflowExecutionContext`:

```csharp
// IWorkflowInstanceGrain.cs — change line 15 from:
    [ReadOnly]
    new ValueTask<IWorkflowDefinition> GetWorkflowDefinition();
// to:
    [ReadOnly]
    ValueTask<IWorkflowDefinition> GetWorkflowDefinition();
```

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application/Services/IBoundaryEventStateAccessor.cs \
        src/Fleans/Fleans.Application/Services/IBoundaryEventHandler.cs \
        src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs
git commit -m "refactor: update application-layer interfaces for definition-as-parameter"
```

---

### Task 4: Update BoundaryEventHandler implementation

**Files:**
- Modify: `src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs`

**Step 1: Update all methods to accept and use `IWorkflowDefinition definition`**

```csharp
// BoundaryEventHandler.cs — full file
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Services;

public partial class BoundaryEventHandler : IBoundaryEventHandler
{
    private IBoundaryEventStateAccessor _accessor = null!;
    private ILogger _logger = null!;

    public void Initialize(IBoundaryEventStateAccessor accessor)
    {
        _accessor = accessor;
        _logger = accessor.Logger;
    }

    public async Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId, IWorkflowDefinition definition)
    {
        var attachedActivityId = boundaryTimer.AttachedToActivityId;

        // Check if attached activity is still active (lookup by instance ID)
        var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
            e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
        if (attachedEntry == null)
        {
            LogStaleBoundaryTimerIgnored(boundaryTimer.ActivityId, hostActivityInstanceId);
            return;
        }

        // Interrupt the attached activity
        var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
        await attachedInstance.Cancel($"Interrupted by boundary timer event '{boundaryTimer.ActivityId}'");
        _accessor.State.CompleteEntries([attachedEntry]);

        // Timer fired, so only unsubscribe message boundaries
        await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, definition);
        LogBoundaryTimerInterrupted(boundaryTimer.ActivityId, attachedActivityId);

        // Create and execute boundary timer event instance
        await CreateAndExecuteBoundaryInstanceAsync(boundaryTimer, attachedInstance, definition);
    }

    public async Task HandleBoundaryMessageFiredAsync(MessageBoundaryEvent boundaryMessage, Guid hostActivityInstanceId, IWorkflowDefinition definition)
    {
        var attachedActivityId = boundaryMessage.AttachedToActivityId;

        // Check if attached activity is still active (lookup by instance ID)
        var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
            e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
        if (attachedEntry == null)
        {
            LogStaleBoundaryMessageIgnored(boundaryMessage.ActivityId, hostActivityInstanceId);
            return;
        }

        // Interrupt the attached activity
        var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
        await attachedInstance.Cancel($"Interrupted by boundary message event '{boundaryMessage.ActivityId}'");
        _accessor.State.CompleteEntries([attachedEntry]);

        // Clean up all boundary events for the interrupted activity
        await UnregisterBoundaryTimerRemindersAsync(attachedActivityId, attachedEntry.ActivityInstanceId, definition);
        // Unsubscribe other boundary messages, but skip the one that fired
        // (its subscription was already removed by DeliverMessage, and calling
        // back into the same correlation grain would deadlock)
        var firedMessageDef = definition.Messages.First(m => m.Id == boundaryMessage.MessageDefinitionId);
        await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, definition, skipMessageName: firedMessageDef.Name);
        LogBoundaryMessageInterrupted(boundaryMessage.ActivityId, attachedActivityId);

        // Create and execute boundary message event instance
        await CreateAndExecuteBoundaryInstanceAsync(boundaryMessage, attachedInstance, definition);
    }

    public async Task HandleBoundaryErrorAsync(string activityId, BoundaryErrorEvent boundaryError, Guid activityInstanceId, IWorkflowDefinition definition)
    {
        LogBoundaryEventTriggered(boundaryError.ActivityId, activityId);

        var activityGrain = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(activityInstanceId);
        await CreateAndExecuteBoundaryInstanceAsync(boundaryError, activityGrain, definition);
    }

    public async Task UnregisterBoundaryTimerRemindersAsync(string activityId, Guid hostActivityInstanceId, IWorkflowDefinition definition)
    {
        foreach (var boundaryTimer in definition.Activities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == activityId))
        {
            var callbackGrain = _accessor.GrainFactory.GetGrain<ITimerCallbackGrain>(
                _accessor.State.Id, $"{hostActivityInstanceId}:{boundaryTimer.ActivityId}");
            await callbackGrain.Cancel();
            LogTimerReminderUnregistered(boundaryTimer.ActivityId);
        }
    }

    public async Task UnsubscribeBoundaryMessageSubscriptionsAsync(string activityId, IWorkflowDefinition definition, string? skipMessageName = null)
    {
        foreach (var boundaryMsg in definition.Activities.OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == activityId))
        {
            var messageDef = definition.Messages.FirstOrDefault(m => m.Id == boundaryMsg.MessageDefinitionId);
            if (messageDef?.CorrelationKeyExpression is null) continue;
            if (messageDef.Name == skipMessageName) continue;

            var correlationValue = await _accessor.GetVariable(messageDef.CorrelationKeyExpression);
            if (correlationValue is null) continue;

            var correlationGrain = _accessor.GrainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);
            await correlationGrain.Unsubscribe(correlationValue.ToString()!);
        }
    }

    private async Task CreateAndExecuteBoundaryInstanceAsync(Activity boundaryActivity, IActivityInstanceGrain sourceInstance, IWorkflowDefinition definition)
    {
        var boundaryInstanceId = Guid.NewGuid();
        var boundaryInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(boundaryInstanceId);
        var variablesId = await sourceInstance.GetVariablesStateId();
        await boundaryInstance.SetActivity(boundaryActivity.ActivityId, boundaryActivity.GetType().Name);
        await boundaryInstance.SetVariablesId(variablesId);

        var boundaryEntry = new ActivityInstanceEntry(boundaryInstanceId, boundaryActivity.ActivityId, _accessor.State.Id);
        _accessor.State.AddEntries([boundaryEntry]);

        await boundaryActivity.ExecuteAsync(_accessor.WorkflowExecutionContext, boundaryInstance, definition);
        await _accessor.TransitionToNextActivity();
        await _accessor.ExecuteWorkflow();
    }

    [LoggerMessage(EventId = 1025, Level = LogLevel.Warning, Message = "Stale boundary timer {TimerActivityId} ignored — host activity instance {HostActivityInstanceId} already completed")]
    private partial void LogStaleBoundaryTimerIgnored(string timerActivityId, Guid hostActivityInstanceId);

    [LoggerMessage(EventId = 1026, Level = LogLevel.Warning, Message = "Stale boundary message {MessageActivityId} ignored — host activity instance {HostActivityInstanceId} already completed")]
    private partial void LogStaleBoundaryMessageIgnored(string messageActivityId, Guid hostActivityInstanceId);

    [LoggerMessage(EventId = 1016, Level = LogLevel.Information, Message = "Boundary error event {BoundaryEventId} triggered by failed activity {ActivityId}")]
    private partial void LogBoundaryEventTriggered(string boundaryEventId, string activityId);

    [LoggerMessage(EventId = 1019, Level = LogLevel.Information, Message = "Timer reminder unregistered for activity {TimerActivityId}")]
    private partial void LogTimerReminderUnregistered(string timerActivityId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Boundary timer {BoundaryTimerId} interrupted attached activity {AttachedActivityId}")]
    private partial void LogBoundaryTimerInterrupted(string boundaryTimerId, string attachedActivityId);

    [LoggerMessage(EventId = 1022, Level = LogLevel.Information, Message = "Boundary message {BoundaryMessageId} interrupted attached activity {AttachedActivityId}")]
    private partial void LogBoundaryMessageInterrupted(string boundaryMessageId, string attachedActivityId);
}
```

**Step 2: Commit**

```bash
git add src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs
git commit -m "refactor: update BoundaryEventHandler to accept IWorkflowDefinition parameter"
```

---

### Task 5: Update WorkflowInstance

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`

**Step 1: Remove the `IBoundaryEventStateAccessor.GetWorkflowDefinition()` explicit implementation**

Delete line 31:
```csharp
// DELETE this line:
    async ValueTask<IWorkflowDefinition> IBoundaryEventStateAccessor.GetWorkflowDefinition() => await GetWorkflowDefinition();
```

**Step 2: Update `ExecuteWorkflow` — pass definition to `ExecuteAsync` and `RegisterBoundaryEventsAsync`**

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
            await currentActivity.ExecuteAsync(this, activityState, definition);
            if (currentActivity is IBoundarableActivity boundarable)
            {
                await boundarable.RegisterBoundaryEventsAsync(this, activityState, definition);
            }
        }

        await TransitionToNextActivity();
    }
}
```

**Step 3: Update `TransitionToNextActivity` — pass definition to `GetNextActivities`**

```csharp
private async Task TransitionToNextActivity()
{
    var definition = await GetWorkflowDefinition();
    // ... existing code until:
            var nextActivities = await currentActivity.GetNextActivities(this, activityInstance, definition);
    // ... rest unchanged
}
```

**Step 4: Update `HandleTimerFired` — pass definition to boundary handler**

```csharp
private Task HandleBoundaryTimerFired(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId)
    => _boundaryHandler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostActivityInstanceId, _workflowDefinition!);
```

**Step 5: Update `CompleteActivityState` — pass definition to boundary handler**

```csharp
private async Task CompleteActivityState(string activityId, ExpandoObject variables)
{
    // ... existing code until:
    var definition = await GetWorkflowDefinition();

    // Unregister any boundary timer reminders attached to this activity
    await _boundaryHandler.UnregisterBoundaryTimerRemindersAsync(activityId, entry.ActivityInstanceId, definition);

    // Unsubscribe any boundary message subscriptions attached to this activity
    await _boundaryHandler.UnsubscribeBoundaryMessageSubscriptionsAsync(activityId, definition);
}
```

**Step 6: Update `OnChildWorkflowCompleted` — use definition for cast**

No change needed — it already calls `GetWorkflowDefinition()` and uses the local `definition` variable.

**Step 7: Update `CompleteConditionSequence` — pass definition to `SetConditionResult`**

```csharp
// In CompleteConditionSequence, change the SetConditionResult call:
    isDecisionMade = await gateway.SetConditionResult(
        this, activityInstance, conditionSequenceId, result, definition);
```

**Step 8: Update `FailActivityWithBoundaryCheck` — pass definition to boundary handler**

```csharp
private async Task FailActivityWithBoundaryCheck(string activityId, Exception exception)
{
    await FailActivityState(activityId, exception);

    var definition = await GetWorkflowDefinition();
    // ... existing code ...
    if (boundaryEvent is not null)
    {
        await _boundaryHandler.HandleBoundaryErrorAsync(activityId, boundaryEvent, activityEntry.ActivityInstanceId, definition);
        return;
    }

    await ExecuteWorkflow();
}
```

**Step 9: Update `RegisterMessageSubscription` — already has `GetWorkflowDefinition()` call, no definition param change needed**

This is a method on `IWorkflowExecutionContext` implemented by `WorkflowInstance` directly. It calls `GetWorkflowDefinition()` internally, which is fine — it's the grain's own method. No change needed.

**Step 10: Update `RegisterBoundaryMessageSubscription` — same as above, no change needed**

**Step 11: Update `HandleBoundaryMessageFired` — pass definition to boundary handler**

```csharp
public async Task HandleBoundaryMessageFired(string boundaryActivityId, Guid hostActivityInstanceId)
{
    await EnsureWorkflowDefinitionAsync();
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();

    var definition = await GetWorkflowDefinition();
    var boundaryMessage = definition.GetActivity(boundaryActivityId) as MessageBoundaryEvent
        ?? throw new InvalidOperationException($"Activity '{boundaryActivityId}' is not a MessageBoundaryEvent");

    await _boundaryHandler.HandleBoundaryMessageFiredAsync(boundaryMessage, hostActivityInstanceId, definition);
    await _state.WriteStateAsync();
}
```

**Step 12: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "refactor: update WorkflowInstance to pass definition to activities and handlers"
```

---

### Task 6: Update tests

**Files:**
- Modify: `src/Fleans/Fleans.Domain.Tests/ActivityTestHelper.cs`
- Modify: All 14 test files in `src/Fleans/Fleans.Domain.Tests/`
- Modify: `src/Fleans/Fleans.Application.Tests/BoundaryEventHandlerTests.cs`

**Step 1: Update `ActivityTestHelper.CreateWorkflowContext` — remove `GetWorkflowDefinition()` mock**

```csharp
// ActivityTestHelper.cs — remove line 14:
//     context.GetWorkflowDefinition().Returns(ValueTask.FromResult(definition));
```

The method still takes `definition` parameter (for other test setup), it just no longer mocks `GetWorkflowDefinition()` on the context.

**Step 2: Update all domain test files**

In every test that calls `ExecuteAsync` or `GetNextActivities`, add `definition` as the third argument:

```csharp
// Pattern for all test files — change:
await activity.ExecuteAsync(workflowContext, activityContext);
// to:
await activity.ExecuteAsync(workflowContext, activityContext, definition);

// Change:
await activity.GetNextActivities(workflowContext, activityContext);
// to:
await activity.GetNextActivities(workflowContext, activityContext, definition);
```

Files and their specific changes:

- `StartEventActivityTests.cs`: Add `definition` to `ExecuteAsync` and `GetNextActivities` calls (3 occurrences)
- `EndEventActivityTests.cs`: Add `definition` to `ExecuteAsync` and `GetNextActivities` calls
- `TaskActivityDomainTests.cs`: Add `definition` to calls
- `ScriptTaskActivityTests.cs`: Add `definition` to `ExecuteAsync` calls (2 occurrences)
- `CallActivityDomainTests.cs`: Add `definition` to calls
- `ErrorEventActivityTests.cs`: Add `definition` to calls
- `BoundaryErrorEventDomainTests.cs`: Add `definition` to calls
- `BoundaryTimerEventDomainTests.cs`: Add `definition` to calls
- `MessageBoundaryEventDomainTests.cs`: Add `definition` to calls
- `TimerStartEventDomainTests.cs`: Add `definition` to calls
- `TimerIntermediateCatchEventDomainTests.cs`: Add `definition` to calls
- `MessageIntermediateCatchEventDomainTests.cs`: Add `definition` to calls
- `ExclusiveGatewayActivityTests.cs`: Add `definition` to `ExecuteAsync` and `GetNextActivities` calls (5 occurrences)
- `ParallelGatewayActivityTests.cs`: Add `definition` to `ExecuteAsync` and `GetNextActivities` calls (5 occurrences)

**Step 3: Update `BoundaryEventHandlerTests`**

Remove the `_accessor.GetWorkflowDefinition()` mock from the stale message test and pass definition to handler:

```csharp
[TestMethod]
public async Task HandleBoundaryMessageFired_StaleActivity_ShouldReturnWithoutAction()
{
    // Arrange
    var boundaryMsg = new MessageBoundaryEvent("bm1", "task1", "msg-def-1");
    var hostInstanceId = Guid.NewGuid();

    var definition = Substitute.For<IWorkflowDefinition>();
    definition.Activities.Returns(new List<Activity> { boundaryMsg });

    // Act
    await _handler.HandleBoundaryMessageFiredAsync(boundaryMsg, hostInstanceId, definition);

    // Assert
    await _accessor.DidNotReceive().TransitionToNextActivity();
    await _accessor.DidNotReceive().ExecuteWorkflow();
}
```

For the timer test — add definition parameter:

```csharp
[TestMethod]
public async Task HandleBoundaryTimerFired_StaleActivity_ShouldReturnWithoutAction()
{
    // Arrange — no matching active entry (state has no entries)
    var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
    var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
    var hostInstanceId = Guid.NewGuid();

    var definition = Substitute.For<IWorkflowDefinition>();

    // Act
    await _handler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostInstanceId, definition);

    // Assert — no transition or execution happened
    await _accessor.DidNotReceive().TransitionToNextActivity();
    await _accessor.DidNotReceive().ExecuteWorkflow();
}
```

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain.Tests/ src/Fleans/Fleans.Application.Tests/BoundaryEventHandlerTests.cs
git commit -m "test: update all tests for definition-as-parameter refactoring"
```

---

### Task 7: Build and run all tests

**Step 1: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded. 0 Errors.

**Step 2: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass.

**Step 3: Commit if any fixups were needed**

```bash
# Only if fixes were required
git add -A && git commit -m "fix: address build/test issues from definition-as-parameter refactoring"
```
