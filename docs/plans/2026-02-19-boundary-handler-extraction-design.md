# Boundary Event Handler Extraction

## Problem

`WorkflowInstance` is 860 lines with 11 distinct responsibilities. Boundary event handling (timer, message, error) accounts for ~150 lines and follows the same duplicated pattern across three methods: `HandleBoundaryTimerFired`, `HandleBoundaryMessageFired`, and `FailActivityWithBoundaryCheck`.

Each repeats: check if host activity is active → interrupt it → cleanup other boundary subscriptions → create boundary event instance → execute → transition → continue workflow.

## Design

### Approach

Extract boundary event handling into a `BoundaryEventHandler` service class injected into WorkflowInstance via DI. WorkflowInstance implements `IBoundaryEventStateAccessor` to provide the handler with access to state, grain factory, and workflow operations.

### File structure

```
Fleans.Application/
├── Services/
│   ├── IBoundaryEventHandler.cs
│   ├── IBoundaryEventStateAccessor.cs
│   └── BoundaryEventHandler.cs
└── Grains/
    └── WorkflowInstance.cs  (modified)
```

### IBoundaryEventStateAccessor

Narrow interface that WorkflowInstance implements, exposing only what the handler needs:

```csharp
public interface IBoundaryEventStateAccessor
{
    WorkflowInstanceState State { get; }
    IGrainFactory GrainFactory { get; }
    ILogger Logger { get; }
    ValueTask<IWorkflowDefinition> GetWorkflowDefinition();
    IWorkflowExecutionContext WorkflowExecutionContext { get; }
    Task TransitionToNextActivity();
    Task ExecuteWorkflow();
    Task WriteStateAsync();
}
```

### IBoundaryEventHandler

```csharp
public interface IBoundaryEventHandler
{
    void Initialize(IBoundaryEventStateAccessor accessor);
    Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId);
    Task HandleBoundaryMessageFiredAsync(MessageBoundaryEvent boundaryMessage, Guid hostActivityInstanceId);
    Task HandleBoundaryErrorAsync(string activityId, BoundaryErrorEvent boundaryError, Guid variablesId);
    Task UnregisterBoundaryTimerRemindersAsync(string activityId, Guid hostActivityInstanceId);
    Task UnsubscribeBoundaryMessageSubscriptionsAsync(string activityId, string? skipMessageName = null);
}
```

### Unified core method

All three boundary types delegate to a single pattern:

```csharp
private async Task InterruptAndExecuteBoundaryAsync(
    Activity boundaryActivity,
    Guid hostActivityInstanceId,
    string attachedActivityId,
    BoundaryCleanupOptions cleanup)
{
    // 1. Stale check — return if host activity already completed
    var attachedEntry = _accessor.State.Entries.FirstOrDefault(
        e => e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
    if (attachedEntry == null) return;

    // 2. Interrupt host activity
    var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
    await attachedInstance.Complete();
    _accessor.State.CompleteEntries([attachedEntry]);

    // 3. Cleanup other boundary subscriptions
    await CleanupBoundarySubscriptions(attachedActivityId, cleanup);

    // 4. Create boundary event instance (inherits variables from interrupted activity)
    var boundaryInstanceId = Guid.NewGuid();
    var boundaryInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(boundaryInstanceId);
    var variablesId = await attachedInstance.GetVariablesStateId();
    await boundaryInstance.SetActivity(boundaryActivity.ActivityId, boundaryActivity.GetType().Name);
    await boundaryInstance.SetVariablesId(variablesId);
    _accessor.State.AddEntries([new ActivityInstanceEntry(boundaryInstanceId, boundaryActivity.ActivityId, _accessor.State.Id)]);

    // 5-6. Execute boundary event + transition + continue workflow
    await boundaryActivity.ExecuteAsync(_accessor.WorkflowExecutionContext, boundaryInstance);
    await _accessor.TransitionToNextActivity();
    await _accessor.ExecuteWorkflow();
}
```

### BoundaryCleanupOptions

Captures the differences between timer, message, and error boundary cleanup:

- **Timer boundary fired**: unsubscribe messages only (timer already fired, no need to unregister)
- **Message boundary fired**: unregister timers + unsubscribe other messages (skip the one that fired to avoid deadlock with correlation grain)
- **Error boundary fired**: unregister timers + unsubscribe all messages

### What moves out of WorkflowInstance (~150 lines)

| Method | Lines | Destination |
|--------|-------|-------------|
| `HandleBoundaryTimerFired` | 34 | `BoundaryEventHandler.HandleBoundaryTimerFiredAsync` |
| `HandleBoundaryMessageFired` (body) | 45 | `BoundaryEventHandler.HandleBoundaryMessageFiredAsync` |
| `FailActivityWithBoundaryCheck` (boundary portion) | 38 | `BoundaryEventHandler.HandleBoundaryErrorAsync` |
| `UnregisterBoundaryTimerReminders` | 12 | `BoundaryEventHandler.UnregisterBoundaryTimerRemindersAsync` |
| `UnsubscribeBoundaryMessageSubscriptions` | 18 | `BoundaryEventHandler.UnsubscribeBoundaryMessageSubscriptionsAsync` |

### What stays in WorkflowInstance

- `HandleTimerFired` — routes to boundary handler or handles intermediate catch inline
- `FailActivity` — delegates boundary check to handler
- `CompleteActivityState` — delegates cleanup to handler
- `HandleBoundaryMessageFired` (public grain method) — thin wrapper delegating to handler

### WorkflowInstance integration

```csharp
public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain, IBoundaryEventStateAccessor
{
    private readonly IBoundaryEventHandler _boundaryHandler;

    public WorkflowInstance(..., IBoundaryEventHandler boundaryHandler)
    {
        _boundaryHandler = boundaryHandler;
    }

    public override Task OnActivateAsync(CancellationToken ct)
    {
        _boundaryHandler.Initialize(this);
        return base.OnActivateAsync(ct);
    }
}
```

### Testing

`BoundaryEventHandler` is unit-testable with a mocked `IBoundaryEventStateAccessor` — no TestCluster needed. Existing integration tests pass unchanged since the external grain interface (`IWorkflowInstanceGrain`) does not change.

### What stays the same

- All grain interfaces unchanged
- All external callers unchanged (TimerCallbackGrain, MessageCorrelationGrain)
- WorkflowInstanceState unchanged
- Existing integration tests unchanged
