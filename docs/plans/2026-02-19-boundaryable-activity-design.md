# Boundaryable Activity Refactoring

## Problem

Boundary event registration (timer + message) lives in the base `Activity.ExecuteAsync()`. This means every activity — including `StartEvent`, `EndEvent`, gateways, and boundary events themselves — runs the registration loops even though only tasks and call activities can have boundaries attached in BPMN.

## Design

### New type hierarchy

```
Activity (abstract record)
├── BoundarableActivity (abstract record) : Activity, IBoundarableActivity
│   ├── TaskActivity
│   │   └── ScriptTask
│   └── CallActivity
├── StartEvent
├── EndEvent
├── ErrorEvent
├── Gateway (abstract)
│   └── ConditionalGateway (abstract)
│       ├── ExclusiveGateway
│       └── ParallelGateway
├── TimerStartEvent
├── TimerIntermediateCatchEvent
├── MessageIntermediateCatchEvent
├── BoundaryTimerEvent
├── MessageBoundaryEvent
└── BoundaryErrorEvent
```

### IBoundarableActivity interface

New interface in `Fleans.Domain`:

```csharp
public interface IBoundarableActivity
{
    Task RegisterBoundaryEventsAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext);
}
```

### BoundarableActivity abstract record

New file `Fleans.Domain/Activities/BoundarableActivity.cs`:

```csharp
[GenerateSerializer]
public abstract record BoundarableActivity(string ActivityId)
    : Activity(ActivityId), IBoundarableActivity
{
    public async Task RegisterBoundaryEventsAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext)
    {
        var definition = await workflowContext.GetWorkflowDefinition();
        var hostInstanceId = await activityContext.GetActivityInstanceId();

        foreach (var boundaryTimer in definition.Activities
            .OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterTimerReminder(
                hostInstanceId,
                boundaryTimer.ActivityId,
                boundaryTimer.TimerDefinition.GetDueTime());
        }

        foreach (var boundaryMsg in definition.Activities
            .OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == ActivityId))
        {
            await workflowContext.RegisterBoundaryMessageSubscription(
                hostInstanceId,
                boundaryMsg.ActivityId,
                boundaryMsg.MessageDefinitionId);
        }
    }
}
```

### Changes to Activity.ExecuteAsync

Remove boundary registration code. The method becomes:

```csharp
internal virtual async Task ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext)
{
    var definition = await workflowContext.GetWorkflowDefinition();
    await activityContext.Execute();
    await activityContext.PublishEvent(new WorkflowActivityExecutedEvent(
        await workflowContext.GetWorkflowInstanceId(),
        definition.WorkflowId,
        await activityContext.GetActivityInstanceId(),
        ActivityId,
        GetType().Name));
}
```

### Changes to WorkflowInstance.ExecuteWorkflow

After `ExecuteAsync`, check for `IBoundarableActivity` and call registration:

```csharp
await currentActivity.ExecuteAsync(this, activityState);

if (currentActivity is IBoundarableActivity boundarable)
{
    await boundarable.RegisterBoundaryEventsAsync(this, activityState);
}
```

### Inheritance changes

- `TaskActivity` inherits `BoundarableActivity` instead of `Activity`
- `CallActivity` inherits `BoundarableActivity` instead of `Activity`
- `ScriptTask` is unchanged (inherits `TaskActivity`)

### What stays the same

- All other activity types remain unchanged
- No changes to grain interfaces or test infrastructure
- BoundaryTimerEvent, MessageBoundaryEvent, BoundaryErrorEvent unchanged
