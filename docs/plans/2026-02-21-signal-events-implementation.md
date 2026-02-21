# Signal Events Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add BPMN Signal Events (intermediate catch, boundary, intermediate throw) with broadcast delivery via a SignalCorrelationGrain.

**Architecture:** Mirrors the existing Message Events pattern. One `SignalCorrelationGrain` per signal name manages a list of subscribers. `BroadcastSignal()` delivers to all subscribers in parallel via `Task.WhenAll`. Activities register/unsubscribe through `IWorkflowExecutionContext` methods on the `WorkflowInstance` grain.

**Tech Stack:** Orleans 9.2.1, .NET 10, C# 14, EF Core (SQLite), MSTest + Orleans.TestingHost

**Design doc:** `docs/plans/2026-02-21-signal-events-design.md`

---

### Task 1: Domain Types — SignalDefinition + SignalCorrelationState

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/SignalDefinition.cs`
- Create: `src/Fleans/Fleans.Domain/States/SignalCorrelationState.cs`
- Modify: `src/Fleans/Fleans.Domain/GrainStorageNames.cs:13` — add `SignalCorrelations` constant

**Step 1: Create SignalDefinition**

```csharp
// src/Fleans/Fleans.Domain/Activities/SignalDefinition.cs
namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record SignalDefinition(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name);
```

Reference: `MessageDefinition.cs` — same pattern but no `CorrelationKeyExpression`.

**Step 2: Create SignalCorrelationState**

```csharp
// src/Fleans/Fleans.Domain/States/SignalCorrelationState.cs
namespace Fleans.Domain.States;

[GenerateSerializer]
public class SignalCorrelationState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public List<SignalSubscription> Subscriptions { get; private set; } = new();
}

[GenerateSerializer]
public record SignalSubscription(
    [property: Id(0)] Guid WorkflowInstanceId,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] Guid HostActivityInstanceId);
```

Reference: `MessageCorrelationState.cs` — uses `List` instead of `Dictionary` since signals have no correlation key.

**Step 3: Add GrainStorageNames constant**

In `src/Fleans/Fleans.Domain/GrainStorageNames.cs`, add after line 13 (`MessageCorrelations`):

```csharp
public const string SignalCorrelations = "signalCorrelations";
```

**Step 4: Add Signals to IWorkflowDefinition and WorkflowDefinition**

In `src/Fleans/Fleans.Domain/Definitions/Workflow.cs`:

Add to `IWorkflowDefinition` interface (after line 18, the `Messages` property):
```csharp
List<SignalDefinition> Signals { get; }
```

Add to `WorkflowDefinition` record (after the `Messages` property at line 38):
```csharp
[Id(5)]
public List<SignalDefinition> Signals { get; init; } = [];
```

Note: Need to add `using Fleans.Domain.Activities;` if `SignalDefinition` isn't already imported (check — `MessageDefinition` is in the same namespace so it should already be imported).

**Step 5: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/SignalDefinition.cs \
  src/Fleans/Fleans.Domain/States/SignalCorrelationState.cs \
  src/Fleans/Fleans.Domain/GrainStorageNames.cs \
  src/Fleans/Fleans.Domain/Definitions/Workflow.cs
git commit -m "feat: add SignalDefinition, SignalCorrelationState, and Signals on WorkflowDefinition"
```

---

### Task 2: Domain Activities — Signal Catch, Boundary, and Throw

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/SignalIntermediateCatchEvent.cs`
- Create: `src/Fleans/Fleans.Domain/Activities/SignalBoundaryEvent.cs`
- Create: `src/Fleans/Fleans.Domain/Activities/SignalIntermediateThrowEvent.cs`
- Modify: `src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs:19-21` — add 3 new methods

**Step 1: Add IWorkflowExecutionContext methods**

In `src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs`, add after line 21 (`RegisterBoundaryMessageSubscription`):

```csharp
ValueTask RegisterSignalSubscription(string signalDefinitionId, string activityId);
ValueTask RegisterBoundarySignalSubscription(Guid hostActivityInstanceId, string boundaryActivityId, string signalDefinitionId);
ValueTask ThrowSignal(string signalDefinitionId);
```

**Step 2: Create SignalIntermediateCatchEvent**

```csharp
// src/Fleans/Fleans.Domain/Activities/SignalIntermediateCatchEvent.cs
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record SignalIntermediateCatchEvent(
    string ActivityId,
    [property: Id(1)] string SignalDefinitionId) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        await base.ExecuteAsync(workflowContext, activityContext, definition);
        await workflowContext.RegisterSignalSubscription(SignalDefinitionId, ActivityId);
        // Do NOT call activityContext.Complete() — the signal grain will do that
    }

    internal override Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return Task.FromResult(nextFlow != null ? [nextFlow.Target] : new List<Activity>());
    }
}
```

Reference: `MessageIntermediateCatchEvent.cs` — same pattern but calls `RegisterSignalSubscription` (no `variablesId`).

**Step 3: Create SignalBoundaryEvent**

```csharp
// src/Fleans/Fleans.Domain/Activities/SignalBoundaryEvent.cs
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
// TODO: Support non-interrupting boundary signal events (cancelActivity=false).
// Requires keeping the host activity active and running the boundary path in parallel.
public record SignalBoundaryEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] string SignalDefinitionId) : Activity(ActivityId)
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
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return Task.FromResult(nextFlow != null ? [nextFlow.Target] : new List<Activity>());
    }
}
```

Reference: `MessageBoundaryEvent.cs` — identical pattern but with `SignalDefinitionId`.

**Step 4: Create SignalIntermediateThrowEvent**

```csharp
// src/Fleans/Fleans.Domain/Activities/SignalIntermediateThrowEvent.cs
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record SignalIntermediateThrowEvent(
    string ActivityId,
    [property: Id(1)] string SignalDefinitionId) : Activity(ActivityId)
{
    internal override async Task ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        await base.ExecuteAsync(workflowContext, activityContext, definition);
        await workflowContext.ThrowSignal(SignalDefinitionId);
        await activityContext.Complete();
    }

    internal override Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return Task.FromResult(nextFlow != null ? [nextFlow.Target] : new List<Activity>());
    }
}
```

This is new — no message equivalent. Throws the signal, then completes itself.

**Step 5: Build to verify compilation**

Build will fail at this point because `WorkflowInstance` (which implements `IWorkflowExecutionContext`) doesn't have the new methods yet. That's expected — we'll fix it in Task 4.

Run: `dotnet build src/Fleans/Fleans.Domain/`
Expected: SUCCESS (Domain alone should build since `IWorkflowExecutionContext` is just an interface)

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/SignalIntermediateCatchEvent.cs \
  src/Fleans/Fleans.Domain/Activities/SignalBoundaryEvent.cs \
  src/Fleans/Fleans.Domain/Activities/SignalIntermediateThrowEvent.cs \
  src/Fleans/Fleans.Domain/IWorkflowExecutionContext.cs
git commit -m "feat: add signal activity types and IWorkflowExecutionContext signal methods"
```

---

### Task 3: Application Layer — ISignalCorrelationGrain + SignalCorrelationGrain

**Files:**
- Create: `src/Fleans/Fleans.Application/Grains/ISignalCorrelationGrain.cs`
- Create: `src/Fleans/Fleans.Application/Grains/SignalCorrelationGrain.cs`

**Step 1: Create ISignalCorrelationGrain**

```csharp
// src/Fleans/Fleans.Application/Grains/ISignalCorrelationGrain.cs
namespace Fleans.Application.Grains;

public interface ISignalCorrelationGrain : IGrainWithStringKey
{
    ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId);
    ValueTask Unsubscribe(Guid workflowInstanceId, string activityId);
    ValueTask<int> BroadcastSignal();
}
```

Reference: `IMessageCorrelationGrain.cs` — no correlation key, no variables, returns `int` (delivery count).

**Step 2: Create SignalCorrelationGrain**

```csharp
// src/Fleans/Fleans.Application/Grains/SignalCorrelationGrain.cs
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class SignalCorrelationGrain : Grain, ISignalCorrelationGrain
{
    private readonly IPersistentState<SignalCorrelationState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<SignalCorrelationGrain> _logger;

    public SignalCorrelationGrain(
        [PersistentState("state", GrainStorageNames.SignalCorrelations)]
        IPersistentState<SignalCorrelationState> state,
        IGrainFactory grainFactory,
        ILogger<SignalCorrelationGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId)
    {
        var signalName = this.GetPrimaryKeyString();

        // Check for duplicate subscription (same workflow + activity)
        if (_state.State.Subscriptions.Any(s =>
            s.WorkflowInstanceId == workflowInstanceId && s.ActivityId == activityId))
        {
            LogDuplicateSubscription(signalName, workflowInstanceId, activityId);
            return;
        }

        _state.State.Subscriptions.Add(new SignalSubscription(workflowInstanceId, activityId, hostActivityInstanceId));
        await _state.WriteStateAsync();
        LogSubscribed(signalName, workflowInstanceId, activityId);
    }

    public async ValueTask Unsubscribe(Guid workflowInstanceId, string activityId)
    {
        var signalName = this.GetPrimaryKeyString();
        var removed = _state.State.Subscriptions.RemoveAll(s =>
            s.WorkflowInstanceId == workflowInstanceId && s.ActivityId == activityId);

        if (removed > 0)
        {
            await _state.WriteStateAsync();
            LogUnsubscribed(signalName, workflowInstanceId, activityId);
        }
    }

    public async ValueTask<int> BroadcastSignal()
    {
        var signalName = this.GetPrimaryKeyString();

        if (_state.State.Subscriptions.Count == 0)
        {
            LogBroadcastNoSubscribers(signalName);
            return 0;
        }

        // Snapshot and clear (at-most-once)
        var subscribers = _state.State.Subscriptions.ToList();
        _state.State.Subscriptions.Clear();
        await _state.WriteStateAsync();

        LogBroadcastStarted(signalName, subscribers.Count);

        // Deliver in parallel with per-subscriber error isolation
        var deliveryTasks = subscribers.Select(async sub =>
        {
            try
            {
                var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(sub.WorkflowInstanceId);
                await workflowInstance.HandleSignalDelivery(sub.ActivityId, sub.HostActivityInstanceId);
                return true;
            }
            catch (Exception ex)
            {
                LogDeliveryFailed(signalName, sub.WorkflowInstanceId, sub.ActivityId, ex);
                return false;
            }
        });

        var results = await Task.WhenAll(deliveryTasks);
        var deliveredCount = results.Count(r => r);
        LogBroadcastCompleted(signalName, deliveredCount, subscribers.Count);

        return deliveredCount;
    }

    [LoggerMessage(EventId = 9100, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' subscription registered: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogSubscribed(string signalName, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' subscription removed: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogUnsubscribed(string signalName, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' broadcast started: {SubscriberCount} subscribers")]
    private partial void LogBroadcastStarted(string signalName, int subscriberCount);

    [LoggerMessage(EventId = 9103, Level = LogLevel.Information,
        Message = "Signal '{SignalName}' broadcast completed: {DeliveredCount}/{TotalCount} delivered")]
    private partial void LogBroadcastCompleted(string signalName, int deliveredCount, int totalCount);

    [LoggerMessage(EventId = 9104, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' broadcast — no subscribers")]
    private partial void LogBroadcastNoSubscribers(string signalName);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Warning,
        Message = "Signal '{SignalName}' delivery failed to workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDeliveryFailed(string signalName, Guid workflowInstanceId, string activityId, Exception exception);

    [LoggerMessage(EventId = 9106, Level = LogLevel.Debug,
        Message = "Signal '{SignalName}' duplicate subscription ignored: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDuplicateSubscription(string signalName, Guid workflowInstanceId, string activityId);
}
```

Key differences from `MessageCorrelationGrain`:
- `Subscribe`: no duplicate exception (silently ignores), adds to list
- `Unsubscribe`: matches by `(workflowInstanceId, activityId)` pair
- `BroadcastSignal`: snapshots all subscribers, clears list, delivers in parallel via `Task.WhenAll`

**Step 3: Build to verify compilation**

Run: `dotnet build src/Fleans/Fleans.Application/`
Expected: FAIL — `IWorkflowInstanceGrain` doesn't have `HandleSignalDelivery` yet. We'll add that in Task 4.

If it fails on just the grain reference, that's expected. Check the error matches.

**Step 4: Commit (even if Application doesn't compile yet, Domain + these new files are correct)**

```bash
git add src/Fleans/Fleans.Application/Grains/ISignalCorrelationGrain.cs \
  src/Fleans/Fleans.Application/Grains/SignalCorrelationGrain.cs
git commit -m "feat: add SignalCorrelationGrain with broadcast delivery"
```

---

### Task 4: Wire Signal Methods into WorkflowInstance + IWorkflowInstanceGrain

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs:42-44` — add signal handler methods
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs:591-699` — add signal registration, throw, and delivery methods

**Step 1: Add signal methods to IWorkflowInstanceGrain**

In `src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs`, add after line 44 (`HandleTimerFired`):

```csharp
[AlwaysInterleave]
Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId);
[AlwaysInterleave]
Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId);
```

`[AlwaysInterleave]` is required because a workflow's throw event can trigger delivery back to the same workflow (self-signaling reentrancy).

**Step 2: Add signal implementation methods to WorkflowInstance**

In `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`, add these methods after the `HandleBoundaryMessageFired` method (after line 699):

```csharp
public async ValueTask RegisterSignalSubscription(string signalDefinitionId, string activityId)
{
    var definition = await GetWorkflowDefinition();
    var signalDef = definition.Signals.First(s => s.Id == signalDefinitionId);

    var entry = State.GetFirstActive(activityId)
        ?? throw new InvalidOperationException($"Active entry not found for '{activityId}'");

    await _state.WriteStateAsync();

    var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalDef.Name);

    try
    {
        await signalGrain.Subscribe(this.GetPrimaryKey(), activityId, entry.ActivityInstanceId);
    }
    catch (Exception ex)
    {
        LogSignalSubscriptionFailed(activityId, signalDef.Name, ex);
        await FailActivityWithBoundaryCheck(activityId, ex);
        await _state.WriteStateAsync();
        return;
    }

    LogSignalSubscriptionRegistered(activityId, signalDef.Name);
}

public async ValueTask RegisterBoundarySignalSubscription(Guid hostActivityInstanceId, string boundaryActivityId, string signalDefinitionId)
{
    var definition = await GetWorkflowDefinition();
    var signalDef = definition.Signals.First(s => s.Id == signalDefinitionId);

    var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalDef.Name);

    try
    {
        await signalGrain.Subscribe(this.GetPrimaryKey(), boundaryActivityId, hostActivityInstanceId);
    }
    catch (Exception ex)
    {
        LogSignalSubscriptionFailed(boundaryActivityId, signalDef.Name, ex);
        return;
    }

    LogSignalSubscriptionRegistered(boundaryActivityId, signalDef.Name);
}

public async ValueTask ThrowSignal(string signalDefinitionId)
{
    var definition = await GetWorkflowDefinition();
    var signalDef = definition.Signals.First(s => s.Id == signalDefinitionId);

    var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalDef.Name);
    var deliveredCount = await signalGrain.BroadcastSignal();
    LogSignalThrown(signalDef.Name, deliveredCount);
}

public async Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId)
{
    await EnsureWorkflowDefinitionAsync();
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();

    var definition = await GetWorkflowDefinition();
    var activity = definition.GetActivity(activityId);

    if (activity is SignalBoundaryEvent boundarySignal)
    {
        LogSignalDeliveryBoundary(activityId);
        await _boundaryHandler.HandleBoundarySignalFiredAsync(boundarySignal, hostActivityInstanceId, definition);
    }
    else
    {
        LogSignalDeliveryComplete(activityId);
        await CompleteActivityState(activityId, new ExpandoObject());
        await ExecuteWorkflow();
    }

    await _state.WriteStateAsync();
}

public async Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId)
{
    await EnsureWorkflowDefinitionAsync();
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();

    var definition = await GetWorkflowDefinition();
    var boundarySignal = definition.GetActivity(boundaryActivityId) as SignalBoundaryEvent
        ?? throw new InvalidOperationException($"Activity '{boundaryActivityId}' is not a SignalBoundaryEvent");

    await _boundaryHandler.HandleBoundarySignalFiredAsync(boundarySignal, hostActivityInstanceId, definition);
    await _state.WriteStateAsync();
}
```

Note: `HandleSignalDelivery` completes the catch activity with an empty `ExpandoObject` — signals carry no data.

**Step 3: Add LoggerMessage methods to WorkflowInstance**

Add after line 787 (after `LogJoinGatewayDeduplication`):

```csharp
[LoggerMessage(EventId = 1028, Level = LogLevel.Information,
    Message = "Signal subscription registered for activity {ActivityId}: signalName={SignalName}")]
private partial void LogSignalSubscriptionRegistered(string activityId, string signalName);

[LoggerMessage(EventId = 1029, Level = LogLevel.Warning,
    Message = "Signal subscription failed for activity {ActivityId}: signalName={SignalName}")]
private partial void LogSignalSubscriptionFailed(string activityId, string signalName, Exception exception);

[LoggerMessage(EventId = 1030, Level = LogLevel.Information,
    Message = "Signal thrown: signalName={SignalName}, deliveredTo={DeliveredCount} subscribers")]
private partial void LogSignalThrown(string signalName, int deliveredCount);

[LoggerMessage(EventId = 1031, Level = LogLevel.Information, Message = "Signal delivered as boundary event for activity {ActivityId}")]
private partial void LogSignalDeliveryBoundary(string activityId);

[LoggerMessage(EventId = 1032, Level = LogLevel.Information, Message = "Signal delivered, completing activity {ActivityId}")]
private partial void LogSignalDeliveryComplete(string activityId);
```

**Step 4: Add `using` for `SignalBoundaryEvent`**

Ensure `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs` has `using Fleans.Domain.Activities;` — check if it already exists. It should since `MessageBoundaryEvent` is already referenced.

**Step 5: Build to verify compilation**

Run: `dotnet build src/Fleans/Fleans.Application/`
Expected: FAIL — `IBoundaryEventHandler` doesn't have `HandleBoundarySignalFiredAsync` yet. That's Task 5.

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/IWorkflowInstanceGrain.cs \
  src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "feat: wire signal registration, throw, and delivery into WorkflowInstance"
```

---

### Task 5: BoundaryEventHandler + BoundarableActivity — Signal Support

**Files:**
- Modify: `src/Fleans/Fleans.Application/Services/IBoundaryEventHandler.cs:12-13` — add 2 new methods
- Modify: `src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs:40-114` — add signal handler + unsubscribe + modify timer/message handlers for cross-cleanup
- Modify: `src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs:22-27` — register signal boundary subscriptions

**Step 1: Add signal methods to IBoundaryEventHandler**

In `src/Fleans/Fleans.Application/Services/IBoundaryEventHandler.cs`, add after line 13 (`UnsubscribeBoundaryMessageSubscriptionsAsync`):

```csharp
Task HandleBoundarySignalFiredAsync(SignalBoundaryEvent boundarySignal, Guid hostActivityInstanceId, IWorkflowDefinition definition);
Task UnsubscribeBoundarySignalSubscriptionsAsync(string activityId, IWorkflowDefinition definition, string? skipSignalName = null);
```

Add `using Fleans.Domain.Activities;` if not already present (check — `BoundaryTimerEvent` is already referenced, so the using should exist).

**Step 2: Add HandleBoundarySignalFiredAsync to BoundaryEventHandler**

In `src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs`, add after `HandleBoundaryMessageFiredAsync` (after line 77):

```csharp
public async Task HandleBoundarySignalFiredAsync(SignalBoundaryEvent boundarySignal, Guid hostActivityInstanceId, IWorkflowDefinition definition)
{
    var attachedActivityId = boundarySignal.AttachedToActivityId;

    // Check if attached activity is still active (lookup by instance ID)
    var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
        e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
    if (attachedEntry == null)
    {
        LogStaleBoundarySignalIgnored(boundarySignal.ActivityId, hostActivityInstanceId);
        return;
    }

    // Interrupt the attached activity
    var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
    await attachedInstance.Cancel($"Interrupted by boundary signal event '{boundarySignal.ActivityId}'");
    _accessor.State.CompleteEntries([attachedEntry]);

    // Clean up all boundary events for the interrupted activity
    await UnregisterBoundaryTimerRemindersAsync(attachedActivityId, attachedEntry.ActivityInstanceId, definition);
    var variablesId = await attachedInstance.GetVariablesStateId();
    await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, variablesId, definition);
    // Unsubscribe other signal boundaries, skip the one that fired
    var firedSignalDef = definition.Signals.First(s => s.Id == boundarySignal.SignalDefinitionId);
    await UnsubscribeBoundarySignalSubscriptionsAsync(attachedActivityId, definition, skipSignalName: firedSignalDef.Name);
    LogBoundarySignalInterrupted(boundarySignal.ActivityId, attachedActivityId);

    // Create and execute boundary signal event instance
    await CreateAndExecuteBoundaryInstanceAsync(boundarySignal, attachedInstance, definition);
}
```

**Step 3: Add UnsubscribeBoundarySignalSubscriptionsAsync**

Add after `UnsubscribeBoundaryMessageSubscriptionsAsync` (after line 114):

```csharp
public async Task UnsubscribeBoundarySignalSubscriptionsAsync(string activityId, IWorkflowDefinition definition, string? skipSignalName = null)
{
    foreach (var boundarySignal in definition.Activities.OfType<SignalBoundaryEvent>()
        .Where(bs => bs.AttachedToActivityId == activityId))
    {
        var signalDef = definition.Signals.FirstOrDefault(s => s.Id == boundarySignal.SignalDefinitionId);
        if (signalDef is null) continue;
        if (signalDef.Name == skipSignalName) continue;

        var signalGrain = _accessor.GrainFactory.GetGrain<ISignalCorrelationGrain>(signalDef.Name);
        await signalGrain.Unsubscribe(_accessor.State.Id, boundarySignal.ActivityId);
    }
}
```

**Step 4: Update HandleBoundaryTimerFiredAsync for cross-cleanup**

In `HandleBoundaryTimerFiredAsync` (around line 40), add signal cleanup after `UnsubscribeBoundaryMessageSubscriptionsAsync`. Find this block:

```csharp
// Timer fired, so only unsubscribe message boundaries
var variablesId = await attachedInstance.GetVariablesStateId();
await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, variablesId, definition);
```

Change to:

```csharp
// Timer fired, so unsubscribe message and signal boundaries
var variablesId = await attachedInstance.GetVariablesStateId();
await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, variablesId, definition);
await UnsubscribeBoundarySignalSubscriptionsAsync(attachedActivityId, definition);
```

**Step 5: Update HandleBoundaryMessageFiredAsync for cross-cleanup**

In `HandleBoundaryMessageFiredAsync`, add signal cleanup. Find this block (around line 72):

```csharp
await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, variablesId, definition, skipMessageName: firedMessageDef.Name);
```

Add after it:

```csharp
await UnsubscribeBoundarySignalSubscriptionsAsync(attachedActivityId, definition);
```

**Step 6: Add LoggerMessage methods to BoundaryEventHandler**

Add after the existing log methods (after line 148):

```csharp
[LoggerMessage(EventId = 1033, Level = LogLevel.Warning, Message = "Stale boundary signal {SignalActivityId} ignored — host activity instance {HostActivityInstanceId} already completed")]
private partial void LogStaleBoundarySignalIgnored(string signalActivityId, Guid hostActivityInstanceId);

[LoggerMessage(EventId = 1034, Level = LogLevel.Information, Message = "Boundary signal {BoundarySignalId} interrupted attached activity {AttachedActivityId}")]
private partial void LogBoundarySignalInterrupted(string boundarySignalId, string attachedActivityId);
```

**Step 7: Add using for ISignalCorrelationGrain**

`BoundaryEventHandler` uses `_accessor.GrainFactory.GetGrain<ISignalCorrelationGrain>()` — ensure `using Fleans.Application.Grains;` is present (it should be since `IActivityInstanceGrain` is already used).

**Step 8: Register signal boundary subscriptions in BoundarableActivity**

In `src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs`, add after the message boundary loop (after line 27):

```csharp
foreach (var boundarySignal in definition.Activities.OfType<SignalBoundaryEvent>()
    .Where(bs => bs.AttachedToActivityId == ActivityId))
{
    await workflowContext.RegisterBoundarySignalSubscription(hostInstanceId, boundarySignal.ActivityId, boundarySignal.SignalDefinitionId);
}
```

**Step 9: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS (all interfaces are now satisfied)

**Step 10: Commit**

```bash
git add src/Fleans/Fleans.Application/Services/IBoundaryEventHandler.cs \
  src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs \
  src/Fleans/Fleans.Domain/Activities/BoundarableActivity.cs
git commit -m "feat: add signal support to BoundaryEventHandler and BoundarableActivity"
```

---

### Task 6: Persistence — EfCoreSignalCorrelationGrainStorage

**Files:**
- Create: `src/Fleans/Fleans.Persistence/EfCoreSignalCorrelationGrainStorage.cs`
- Modify: `src/Fleans/Fleans.Persistence/FleanCommandDbContext.cs:16` — add DbSet
- Modify: `src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs:125` — add entity config
- Modify: `src/Fleans/Fleans.Persistence/DependencyInjection.cs:38` — register storage

**Step 1: Create EfCoreSignalCorrelationGrainStorage**

```csharp
// src/Fleans/Fleans.Persistence/EfCoreSignalCorrelationGrainStorage.cs
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreSignalCorrelationGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreSignalCorrelationGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();

        var state = await db.SignalCorrelations.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == id);

        if (state is not null)
        {
            grainState.State = (T)(object)state;
            grainState.ETag = state.ETag;
            grainState.RecordExists = true;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = (SignalCorrelationState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.SignalCorrelations.FindAsync(id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.SignalCorrelations.Add(state);
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.Key).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = newETag;
        }

        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var existing = await db.SignalCorrelations.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.SignalCorrelations.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
```

Reference: exact mirror of `EfCoreMessageCorrelationGrainStorage.cs` with `SignalCorrelations` / `SignalCorrelationState`.

**Step 2: Add DbSet to FleanCommandDbContext**

In `src/Fleans/Fleans.Persistence/FleanCommandDbContext.cs`, add after line 16 (`MessageCorrelations`):

```csharp
public DbSet<SignalCorrelationState> SignalCorrelations => Set<SignalCorrelationState>();
```

**Step 3: Add entity config to FleanModelConfiguration**

In `src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs`, add after line 125 (after the `MessageCorrelationState` entity block closing `});`):

```csharp
modelBuilder.Entity<SignalCorrelationState>(entity =>
{
    entity.ToTable("SignalCorrelations");
    entity.HasKey(e => e.Key);

    entity.Property(e => e.Key).HasMaxLength(512);
    entity.Property(e => e.ETag).HasMaxLength(64);

    entity.Property(e => e.Subscriptions)
        .HasColumnName("Subscriptions")
        .HasConversion(
            v => JsonConvert.SerializeObject(v),
            v => JsonConvert.DeserializeObject<List<SignalSubscription>>(v)
                ?? new List<SignalSubscription>());
});
```

Note: uses `List<SignalSubscription>` (not `Dictionary`) for the JSON conversion.

**Step 4: Register storage in DependencyInjection**

In `src/Fleans/Fleans.Persistence/DependencyInjection.cs`, add after line 38 (after `MessageCorrelations` registration):

```csharp
services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.SignalCorrelations,
    (sp, _) => new EfCoreSignalCorrelationGrainStorage(
        sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));
```

**Step 5: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Persistence/EfCoreSignalCorrelationGrainStorage.cs \
  src/Fleans/Fleans.Persistence/FleanCommandDbContext.cs \
  src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs \
  src/Fleans/Fleans.Persistence/DependencyInjection.cs
git commit -m "feat: add EfCore persistence for SignalCorrelationGrain"
```

---

### Task 7: BPMN Parser — Signal Definitions and Activities

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs`

**Step 1: Add ParseSignals method**

Add after `ParseMessages` method (after line 368):

```csharp
private static List<SignalDefinition> ParseSignals(XDocument doc)
{
    var signals = new List<SignalDefinition>();
    foreach (var signalEl in doc.Root!.Elements(Bpmn + "signal"))
    {
        var id = signalEl.Attribute("id")?.Value
            ?? throw new InvalidOperationException("signal element must have an id attribute");
        var name = signalEl.Attribute("name")?.Value
            ?? throw new InvalidOperationException($"signal '{id}' must have a name attribute");

        signals.Add(new SignalDefinition(id, name));
    }
    return signals;
}
```

**Step 2: Call ParseSignals from ConvertFromXml**

In `ConvertFromXml` method, add after line 43 (`var messages = ParseMessages(doc);`):

```csharp
var signals = ParseSignals(doc);
```

**Step 3: Pass signals to WorkflowDefinition**

In `ConvertFromXml`, modify the `WorkflowDefinition` creation (line 51-57) to include `Signals`:

Find:
```csharp
var workflow = new WorkflowDefinition
{
    WorkflowId = workflowId,
    Activities = activities,
    SequenceFlows = sequenceFlows,
    Messages = messages
};
```

Replace with:
```csharp
var workflow = new WorkflowDefinition
{
    WorkflowId = workflowId,
    Activities = activities,
    SequenceFlows = sequenceFlows,
    Messages = messages,
    Signals = signals
};
```

**Step 4: Parse signal intermediate catch events**

In the intermediate catch event parsing block (line 86-113), add a `signalDef` check. Find the `else` block at line 108-112:

```csharp
else
{
    throw new InvalidOperationException(
        $"IntermediateCatchEvent '{id}' has an unsupported event definition.");
}
```

Change to:
```csharp
else
{
    var signalDef = catchEvent.Element(Bpmn + "signalEventDefinition");
    if (signalDef != null)
    {
        var signalRef = signalDef.Attribute("signalRef")?.Value
            ?? throw new InvalidOperationException(
                $"IntermediateCatchEvent '{id}' signalEventDefinition must have a signalRef attribute");
        var activity = new SignalIntermediateCatchEvent(id, signalRef);
        activities.Add(activity);
        activityMap[id] = activity;
    }
    else
    {
        throw new InvalidOperationException(
            $"IntermediateCatchEvent '{id}' has an unsupported event definition.");
    }
}
```

**Step 5: Parse intermediate throw events**

Add a new section after intermediate catch events (after line 113). This is entirely new — no throw events exist yet:

```csharp
// Parse intermediate throw events (signal)
foreach (var throwEvent in process.Descendants(Bpmn + "intermediateThrowEvent"))
{
    var id = GetId(throwEvent);
    var signalDef = throwEvent.Element(Bpmn + "signalEventDefinition");

    if (signalDef != null)
    {
        var signalRef = signalDef.Attribute("signalRef")?.Value
            ?? throw new InvalidOperationException(
                $"IntermediateThrowEvent '{id}' signalEventDefinition must have a signalRef attribute");
        var activity = new SignalIntermediateThrowEvent(id, signalRef);
        activities.Add(activity);
        activityMap[id] = activity;
    }
    else
    {
        throw new InvalidOperationException(
            $"IntermediateThrowEvent '{id}' has an unsupported event definition.");
    }
}
```

**Step 6: Parse signal boundary events**

In the boundary event parsing block (line 252-284), add `signalDef` check. Find this area:

```csharp
var timerDef = boundaryEl.Element(Bpmn + "timerEventDefinition");
var errorDef = boundaryEl.Element(Bpmn + "errorEventDefinition");
var messageDef = boundaryEl.Element(Bpmn + "messageEventDefinition");
```

Add after `messageDef`:
```csharp
var signalDef = boundaryEl.Element(Bpmn + "signalEventDefinition");
```

Then in the if-else chain, add a `signalDef` branch before the else. Find:

```csharp
else
{
    string? errorCode = errorDef?.Attribute("errorRef")?.Value;
    activity = new BoundaryErrorEvent(id, attachedToRef, errorCode);
}
```

Change to:
```csharp
else if (signalDef != null)
{
    var signalRef = signalDef.Attribute("signalRef")?.Value
        ?? throw new InvalidOperationException(
            $"boundaryEvent '{id}' signalEventDefinition must have a signalRef attribute");
    activity = new SignalBoundaryEvent(id, attachedToRef, signalRef);
}
else
{
    string? errorCode = errorDef?.Attribute("errorRef")?.Value;
    activity = new BoundaryErrorEvent(id, attachedToRef, errorCode);
}
```

**Step 7: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS

**Step 8: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs
git commit -m "feat: parse BPMN signal definitions, catch, throw, and boundary events"
```

---

### Task 8: API Endpoint + DTOs

**Files:**
- Create: `src/Fleans/Fleans.ServiceDefaults/DTOs/SendSignalRequest.cs`
- Create: `src/Fleans/Fleans.ServiceDefaults/DTOs/SendSignalResponse.cs`
- Modify: `src/Fleans/Fleans.Api/Controllers/WorkflowController.cs:81-84` — add SendSignal endpoint

**Step 1: Create SendSignalRequest**

```csharp
// src/Fleans/Fleans.ServiceDefaults/DTOs/SendSignalRequest.cs
namespace Fleans.ServiceDefaults.DTOs;

public record SendSignalRequest(string SignalName);
```

**Step 2: Create SendSignalResponse**

```csharp
// src/Fleans/Fleans.ServiceDefaults/DTOs/SendSignalResponse.cs
namespace Fleans.ServiceDefaults.DTOs;

public record SendSignalResponse(int DeliveredCount);
```

**Step 3: Add SendSignal endpoint to WorkflowController**

In `src/Fleans/Fleans.Api/Controllers/WorkflowController.cs`, add after line 81 (after `SendMessage` method closing brace):

```csharp
[HttpPost("signal", Name = "SendSignal")]
public async Task<IActionResult> SendSignal([FromBody] SendSignalRequest request)
{
    if (request == null || string.IsNullOrWhiteSpace(request.SignalName))
        return BadRequest(new ErrorResponse("SignalName is required"));

    try
    {
        var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(request.SignalName);
        var deliveredCount = await signalGrain.BroadcastSignal();
        return Ok(new SendSignalResponse(DeliveredCount: deliveredCount));
    }
    catch (Exception ex)
    {
        LogSignalDeliveryError(ex);
        return StatusCode(500, new ErrorResponse("An error occurred while broadcasting the signal"));
    }
}
```

**Step 4: Add LoggerMessage for signal delivery error**

Add after line 84 (after `LogMessageDeliveryError`):

```csharp
[LoggerMessage(EventId = 8003, Level = LogLevel.Error, Message = "Error broadcasting signal")]
private partial void LogSignalDeliveryError(Exception exception);
```

**Step 5: Add using for ISignalCorrelationGrain**

Ensure `using Fleans.Application.Grains;` is present (it should be since `IMessageCorrelationGrain` is already referenced).

**Step 6: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS

**Step 7: Commit**

```bash
git add src/Fleans/Fleans.ServiceDefaults/DTOs/SendSignalRequest.cs \
  src/Fleans/Fleans.ServiceDefaults/DTOs/SendSignalResponse.cs \
  src/Fleans/Fleans.Api/Controllers/WorkflowController.cs
git commit -m "feat: add POST /signal API endpoint for broadcasting signals"
```

---

### Task 9: Test Infrastructure — Register Signal Storage in TestCluster

**Files:**
- Modify: `src/Fleans/Fleans.Application.Tests/WorkflowTestBase.cs:62` — add signal correlations memory storage

**Step 1: Add memory storage for signal correlations**

In `src/Fleans/Fleans.Application.Tests/WorkflowTestBase.cs`, add after line 62 (`.AddMemoryGrainStorage(GrainStorageNames.MessageCorrelations)`):

```csharp
.AddMemoryGrainStorage(GrainStorageNames.SignalCorrelations)
```

**Step 2: Build tests to verify compilation**

Run: `dotnet build src/Fleans/Fleans.Application.Tests/`
Expected: SUCCESS

**Step 3: Run existing tests to make sure nothing is broken**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/WorkflowTestBase.cs
git commit -m "chore: register signal correlations memory storage in TestCluster"
```

---

### Task 10: Tests — Signal Intermediate Catch Event

**Files:**
- Create: `src/Fleans/Fleans.Application.Tests/SignalIntermediateCatchEventTests.cs`

**Step 1: Write the test file**

```csharp
// src/Fleans/Fleans.Application.Tests/SignalIntermediateCatchEventTests.cs
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class SignalIntermediateCatchEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task SignalCatch_ShouldSuspendWorkflow_UntilSignalBroadcast()
    {
        // Arrange — Start → Task → SignalCatch → End
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalDef = new SignalDefinition("sig1", "orderApproved");
        var signalCatch = new SignalIntermediateCatchEvent("waitApproval", "sig1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "signal-catch-test",
            Activities = [start, task, signalCatch, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, signalCatch),
                new SequenceFlow("f3", signalCatch, end)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — workflow suspended at signal catch
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should NOT be completed — waiting for signal");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "waitApproval"),
            "Signal catch activity should be active");

        // Act — broadcast signal via correlation grain
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("orderApproved");
        var deliveredCount = await signalGrain.BroadcastSignal();

        // Assert — workflow completed
        Assert.AreEqual(1, deliveredCount, "Should deliver to one subscriber");
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed after signal broadcast");
    }

    [TestMethod]
    public async Task SignalCatch_MultipleSubscribers_AllShouldReceive()
    {
        // Arrange — two workflows waiting for the same signal
        var signalDef = new SignalDefinition("sig1", "globalNotify");

        WorkflowDefinition CreateWorkflow(string id) => new()
        {
            WorkflowId = id,
            Activities =
            [
                new StartEvent("start"),
                new SignalIntermediateCatchEvent("waitSignal", "sig1"),
                new EndEvent("end")
            ],
            SequenceFlows =
            [
                new SequenceFlow("f1", new StartEvent("start"), new SignalIntermediateCatchEvent("waitSignal", "sig1")),
                new SequenceFlow("f2", new SignalIntermediateCatchEvent("waitSignal", "sig1"), new EndEvent("end"))
            ],
            Signals = [signalDef]
        };

        var instance1 = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance1.SetWorkflow(CreateWorkflow("multi-signal-1"));
        await instance1.StartWorkflow();

        var instance2 = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance2.SetWorkflow(CreateWorkflow("multi-signal-2"));
        await instance2.StartWorkflow();

        // Both should be waiting
        var snap1 = await QueryService.GetStateSnapshot(instance1.GetPrimaryKey());
        var snap2 = await QueryService.GetStateSnapshot(instance2.GetPrimaryKey());
        Assert.IsFalse(snap1!.IsCompleted);
        Assert.IsFalse(snap2!.IsCompleted);

        // Act — broadcast signal
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("globalNotify");
        var deliveredCount = await signalGrain.BroadcastSignal();

        // Assert — both workflows completed
        Assert.AreEqual(2, deliveredCount, "Should deliver to two subscribers");
        var final1 = await QueryService.GetStateSnapshot(instance1.GetPrimaryKey());
        var final2 = await QueryService.GetStateSnapshot(instance2.GetPrimaryKey());
        Assert.IsTrue(final1!.IsCompleted, "Instance 1 should be completed");
        Assert.IsTrue(final2!.IsCompleted, "Instance 2 should be completed");
    }

    [TestMethod]
    public async Task SignalBroadcast_NoSubscribers_ShouldReturnZero()
    {
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("nonexistentSignal");
        var deliveredCount = await signalGrain.BroadcastSignal();
        Assert.AreEqual(0, deliveredCount, "No subscribers should mean zero deliveries");
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~SignalIntermediateCatchEventTests"`
Expected: ALL 3 PASS

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/SignalIntermediateCatchEventTests.cs
git commit -m "test: add signal intermediate catch event tests"
```

---

### Task 11: Tests — Signal Boundary Event

**Files:**
- Create: `src/Fleans/Fleans.Application.Tests/SignalBoundaryEventTests.cs`

**Step 1: Write the test file**

```csharp
// src/Fleans/Fleans.Application.Tests/SignalBoundaryEventTests.cs
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class SignalBoundaryEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task BoundarySignal_SignalArrivesFirst_ShouldFollowBoundaryFlow()
    {
        // Arrange — Start → Task(+BoundarySignal) → End, BoundarySignal → Recovery → SigEnd
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalDef = new SignalDefinition("sig1", "cancelOrder");
        var boundarySignal = new SignalBoundaryEvent("bsig1", "task1", "sig1");
        var end = new EndEvent("end");
        var recovery = new TaskActivity("recovery");
        var sigEnd = new EndEvent("sigEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-signal-test",
            Activities = [start, task, boundarySignal, end, recovery, sigEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundarySignal, recovery),
                new SequenceFlow("f4", recovery, sigEnd)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Verify task is active
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(preSnapshot!.ActiveActivities.Any(a => a.ActivityId == "task1"),
            "Task should be active");

        // Act — broadcast signal
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("cancelOrder");
        var deliveredCount = await signalGrain.BroadcastSignal();

        // Assert — boundary path taken, task interrupted
        Assert.AreEqual(1, deliveredCount, "Should deliver to one subscriber");
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsFalse(snapshot!.IsCompleted, "Workflow should NOT be completed yet — recovery pending");
        var interruptedTask = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityId == "task1");
        Assert.IsNotNull(interruptedTask, "Original task should be completed (interrupted)");
        Assert.IsTrue(interruptedTask.IsCancelled, "Interrupted task should be cancelled");
        Assert.IsTrue(snapshot.ActiveActivities.Any(a => a.ActivityId == "recovery"),
            "Recovery task should be active");

        // Complete recovery
        await workflowInstance.CompleteActivity("recovery", new ExpandoObject());
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should be completed after recovery");
        Assert.IsTrue(finalSnapshot.CompletedActivities.Any(a => a.ActivityId == "sigEnd"),
            "Should complete via signal end");
    }

    [TestMethod]
    public async Task BoundarySignal_TaskCompletesFirst_ShouldFollowNormalFlow()
    {
        // Arrange
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var signalDef = new SignalDefinition("sig1", "cancelOrder");
        var boundarySignal = new SignalBoundaryEvent("bsig1", "task1", "sig1");
        var end = new EndEvent("end");
        var sigEnd = new EndEvent("sigEnd");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "boundary-signal-normal",
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

        // Act — complete task normally
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());

        // Assert — normal flow, subscription cleaned up
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed via normal flow");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "Should complete via normal end event");

        // Verify subscription is gone
        var signalGrain = Cluster.GrainFactory.GetGrain<ISignalCorrelationGrain>("cancelOrder");
        var deliveredCount = await signalGrain.BroadcastSignal();
        Assert.AreEqual(0, deliveredCount, "Subscription should have been cleaned up");
    }

    [TestMethod]
    public async Task BoundarySignal_StaleSignal_ShouldBeSilentlyIgnored()
    {
        // Arrange
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

        // Get the host activity instance ID
        var instanceId = workflowInstance.GetPrimaryKey();
        var preSnapshot = await QueryService.GetStateSnapshot(instanceId);
        var hostInstanceId = preSnapshot!.ActiveActivities.First(a => a.ActivityId == "task1").ActivityInstanceId;

        // Complete task normally
        await workflowInstance.CompleteActivity("task1", new ExpandoObject());
        var midSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(midSnapshot!.IsCompleted, "Workflow should be completed via normal flow");

        // Act — simulate stale boundary signal
        await workflowInstance.HandleBoundarySignalFired("bsig1", hostInstanceId);

        // Assert — workflow is still completed, no crash
        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(finalSnapshot!.IsCompleted, "Workflow should still be completed after stale signal");
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~SignalBoundaryEventTests"`
Expected: ALL 3 PASS

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/SignalBoundaryEventTests.cs
git commit -m "test: add signal boundary event tests"
```

---

### Task 12: Tests — Signal Throw Event + Self-Signaling

**Files:**
- Create: `src/Fleans/Fleans.Application.Tests/SignalIntermediateThrowEventTests.cs`

**Step 1: Write the test file**

```csharp
// src/Fleans/Fleans.Application.Tests/SignalIntermediateThrowEventTests.cs
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class SignalIntermediateThrowEventTests : WorkflowTestBase
{
    [TestMethod]
    public async Task SignalThrow_ShouldCompleteAndContinue()
    {
        // Arrange — Start → ThrowSignal → End
        var start = new StartEvent("start");
        var signalDef = new SignalDefinition("sig1", "orderApproved");
        var throwEvent = new SignalIntermediateThrowEvent("emitApproval", "sig1");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "signal-throw-test",
            Activities = [start, throwEvent, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, throwEvent),
                new SequenceFlow("f2", throwEvent, end)
            ],
            Signals = [signalDef]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Assert — workflow completed (throw event completes immediately)
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.IsCompleted, "Workflow should be completed");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "emitApproval"),
            "Throw event should be completed");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "End event should be completed");
    }

    [TestMethod]
    public async Task SignalThrow_ShouldDeliverToCatchingWorkflow()
    {
        // Arrange — Workflow A throws signal, Workflow B catches it
        var signalDef = new SignalDefinition("sig1", "orderApproved");

        // Workflow B — waiting for signal
        var catchWorkflow = new WorkflowDefinition
        {
            WorkflowId = "signal-catcher",
            Activities =
            [
                new StartEvent("start"),
                new SignalIntermediateCatchEvent("waitApproval", "sig1"),
                new EndEvent("end")
            ],
            SequenceFlows =
            [
                new SequenceFlow("f1", new StartEvent("start"), new SignalIntermediateCatchEvent("waitApproval", "sig1")),
                new SequenceFlow("f2", new SignalIntermediateCatchEvent("waitApproval", "sig1"), new EndEvent("end"))
            ],
            Signals = [signalDef]
        };

        var catchInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await catchInstance.SetWorkflow(catchWorkflow);
        await catchInstance.StartWorkflow();

        // Verify catcher is waiting
        var catchId = catchInstance.GetPrimaryKey();
        var catchSnap = await QueryService.GetStateSnapshot(catchId);
        Assert.IsFalse(catchSnap!.IsCompleted);

        // Workflow A — throws signal
        var throwWorkflow = new WorkflowDefinition
        {
            WorkflowId = "signal-thrower",
            Activities =
            [
                new StartEvent("start"),
                new SignalIntermediateThrowEvent("emitApproval", "sig1"),
                new EndEvent("end")
            ],
            SequenceFlows =
            [
                new SequenceFlow("f1", new StartEvent("start"), new SignalIntermediateThrowEvent("emitApproval", "sig1")),
                new SequenceFlow("f2", new SignalIntermediateThrowEvent("emitApproval", "sig1"), new EndEvent("end"))
            ],
            Signals = [signalDef]
        };

        var throwInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await throwInstance.SetWorkflow(throwWorkflow);
        await throwInstance.StartWorkflow();

        // Assert — both workflows completed
        var throwSnap = await QueryService.GetStateSnapshot(throwInstance.GetPrimaryKey());
        Assert.IsTrue(throwSnap!.IsCompleted, "Thrower should be completed");

        var catchFinal = await QueryService.GetStateSnapshot(catchId);
        Assert.IsTrue(catchFinal!.IsCompleted, "Catcher should be completed after signal delivery");
    }
}
```

Note: Self-signaling test (workflow throws signal that it also catches) is intentionally omitted because it would require the workflow to have both a catch and throw event for the same signal in sequence, and the catch event would need to be active (subscribed) before the throw executes. That's a complex flow arrangement — if needed later, it can be added as a separate test.

**Step 2: Run tests to verify they pass**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~SignalIntermediateThrowEventTests"`
Expected: ALL 2 PASS

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/SignalIntermediateThrowEventTests.cs
git commit -m "test: add signal throw event and cross-workflow delivery tests"
```

---

### Task 13: Tests — BPMN Parsing

**Files:**
- Create: `src/Fleans/Fleans.Infrastructure.Tests/BpmnSignalParsingTests.cs` (or add to existing BpmnConverter tests if they exist)

**Step 1: Check if BpmnConverter tests exist**

Look for: `src/Fleans/Fleans.Infrastructure.Tests/` or similar. If a test project doesn't exist, add tests to `Fleans.Application.Tests` since it already references Infrastructure.

**Step 2: Write the BPMN parsing test**

```csharp
using Fleans.Domain.Activities;
using Fleans.Infrastructure.Bpmn;

namespace Fleans.Application.Tests;

[TestClass]
public class BpmnSignalParsingTests
{
    [TestMethod]
    public async Task ParseBpmn_SignalCatchEvent_ShouldCreateSignalIntermediateCatchEvent()
    {
        var bpmn = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <signal id="Signal_1" name="orderApproved" />
              <process id="process1">
                <startEvent id="start" />
                <intermediateCatchEvent id="waitSignal">
                  <signalEventDefinition signalRef="Signal_1" />
                </intermediateCatchEvent>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="waitSignal" />
                <sequenceFlow id="f2" sourceRef="waitSignal" targetRef="end" />
              </process>
            </definitions>
            """;

        var converter = new BpmnConverter();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        Assert.AreEqual(1, workflow.Signals.Count);
        Assert.AreEqual("Signal_1", workflow.Signals[0].Id);
        Assert.AreEqual("orderApproved", workflow.Signals[0].Name);

        var catchEvent = workflow.Activities.OfType<SignalIntermediateCatchEvent>().Single();
        Assert.AreEqual("waitSignal", catchEvent.ActivityId);
        Assert.AreEqual("Signal_1", catchEvent.SignalDefinitionId);
    }

    [TestMethod]
    public async Task ParseBpmn_SignalThrowEvent_ShouldCreateSignalIntermediateThrowEvent()
    {
        var bpmn = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <signal id="Signal_1" name="orderApproved" />
              <process id="process1">
                <startEvent id="start" />
                <intermediateThrowEvent id="emitSignal">
                  <signalEventDefinition signalRef="Signal_1" />
                </intermediateThrowEvent>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="emitSignal" />
                <sequenceFlow id="f2" sourceRef="emitSignal" targetRef="end" />
              </process>
            </definitions>
            """;

        var converter = new BpmnConverter();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        var throwEvent = workflow.Activities.OfType<SignalIntermediateThrowEvent>().Single();
        Assert.AreEqual("emitSignal", throwEvent.ActivityId);
        Assert.AreEqual("Signal_1", throwEvent.SignalDefinitionId);
    }

    [TestMethod]
    public async Task ParseBpmn_SignalBoundaryEvent_ShouldCreateSignalBoundaryEvent()
    {
        var bpmn = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <signal id="Signal_1" name="cancelOrder" />
              <process id="process1">
                <startEvent id="start" />
                <task id="task1" />
                <boundaryEvent id="bsig1" attachedToRef="task1">
                  <signalEventDefinition signalRef="Signal_1" />
                </boundaryEvent>
                <endEvent id="end" />
                <endEvent id="sigEnd" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="task1" />
                <sequenceFlow id="f2" sourceRef="task1" targetRef="end" />
                <sequenceFlow id="f3" sourceRef="bsig1" targetRef="sigEnd" />
              </process>
            </definitions>
            """;

        var converter = new BpmnConverter();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        var boundaryEvent = workflow.Activities.OfType<SignalBoundaryEvent>().Single();
        Assert.AreEqual("bsig1", boundaryEvent.ActivityId);
        Assert.AreEqual("task1", boundaryEvent.AttachedToActivityId);
        Assert.AreEqual("Signal_1", boundaryEvent.SignalDefinitionId);
    }
}
```

**Step 3: Run tests to verify they pass**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~BpmnSignalParsingTests"`
Expected: ALL 3 PASS

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/BpmnSignalParsingTests.cs
git commit -m "test: add BPMN signal parsing tests"
```

---

### Task 14: Update README — BPMN Elements Table

**Files:**
- Modify: `README.md:32` — update Signal Event row

**Step 1: Update Signal Event row in README**

Find the Signal Event row (line 32):

```
| Signal Event         | Represents the sending or receiving of a signal.                             |             |
```

Replace with:

```
| Signal Event         | Represents the sending or receiving of a signal.                             | Implemented |
```

**Step 2: Commit**

```bash
git add README.md
git commit -m "docs: mark Signal Event as implemented in BPMN elements table"
```

---

### Task 15: Full Test Suite Run + Final Verification

**Step 1: Build entire solution**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS

**Step 2: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS (both existing tests and new signal tests)

**Step 3: Run only signal tests to confirm count**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~Signal" -v n`
Expected: 11 tests pass (3 catch + 3 boundary + 2 throw + 3 parsing)

**Step 4: Commit any remaining changes if needed**

If clean, no commit needed.
