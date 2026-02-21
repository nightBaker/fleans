# Signal Events Design

**Date:** 2026-02-21
**Audit items:** 1.5 (Signal broadcast mechanism), 1.6 (Signal Events)

## Problem

BPMN signals are broadcast events — one signal wakes all waiting workflows. The engine currently has no signal support. Messages (point-to-point, correlated) are implemented; signals follow the same grain pattern but with broadcast semantics.

## Scope

Three BPMN elements:

| Element | BPMN XML | Behavior |
|---------|----------|----------|
| Signal Intermediate Catch Event | `<intermediateCatchEvent>` + `<signalEventDefinition>` | Workflow pauses, waits for signal, resumes when thrown |
| Signal Boundary Event | `<boundaryEvent>` + `<signalEventDefinition>` | Interrupts host activity when signal fires |
| Signal Intermediate Throw Event | `<intermediateThrowEvent>` + `<signalEventDefinition>` | Broadcasts signal to all waiting workflows |

Not in scope: `SignalStartEvent` (starting new workflow instances on signal).

## Approach: SignalCorrelationGrain

Mirror the `MessageCorrelationGrain` pattern. One grain per signal name, keyed by `signalName` (string). Maintains a list of subscribers. When a signal is thrown, deliver to all subscribers in parallel.

### Key differences from messages

| Aspect | Messages | Signals |
|--------|----------|---------|
| Grain key | Message name | Signal name |
| Subscription identity | `correlationKey` (unique) | `(workflowInstanceId, activityId)` pair |
| Subscriber count | Exactly one per correlation key | Many per signal name |
| Delivery | Point-to-point, returns bool | Broadcast via `Task.WhenAll`, returns count |
| Payload | Variables (`ExpandoObject`) | None (pure notification) |
| Correlation | Required (expression evaluated against variables) | Not applicable |

## Domain Layer

### New types

**`SignalDefinition`** (`Fleans.Domain/Activities/SignalDefinition.cs`):
```csharp
[GenerateSerializer]
public record SignalDefinition(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name);
```

**`SignalIntermediateCatchEvent`** (`Fleans.Domain/Activities/SignalIntermediateCatchEvent.cs`):
- Record extending `Activity`, holds `SignalDefinitionId`
- `ExecuteAsync`: calls `workflowContext.RegisterSignalSubscription(SignalDefinitionId, ActivityId)`, does NOT complete (grain will complete it on delivery)
- `GetNextActivities`: returns single next sequence flow

**`SignalBoundaryEvent`** (`Fleans.Domain/Activities/SignalBoundaryEvent.cs`):
- Record extending `Activity`, holds `AttachedToActivityId`, `SignalDefinitionId`
- `ExecuteAsync`: calls `base.ExecuteAsync()`, then `activityContext.Complete()`
- `GetNextActivities`: returns single next sequence flow

**`SignalIntermediateThrowEvent`** (`Fleans.Domain/Activities/SignalIntermediateThrowEvent.cs`):
- Record extending `Activity`, holds `SignalDefinitionId`
- `ExecuteAsync`: calls `workflowContext.ThrowSignal(SignalDefinitionId)`, then `activityContext.Complete()`
- `GetNextActivities`: returns single next sequence flow

### New state

**`SignalCorrelationState`** (`Fleans.Domain/States/SignalCorrelationState.cs`):
```csharp
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

Uses `List<SignalSubscription>` (not dictionary) because multiple activities in the same workflow can subscribe to the same signal.

### IWorkflowExecutionContext additions

```csharp
ValueTask RegisterSignalSubscription(string signalDefinitionId, string activityId);
ValueTask RegisterBoundarySignalSubscription(Guid hostActivityInstanceId, string boundaryActivityId, string signalDefinitionId);
ValueTask ThrowSignal(string signalDefinitionId);
```

No `variablesId` parameter — signals have no correlation.

### IWorkflowDefinition additions

```csharp
List<SignalDefinition> Signals { get; }
```

Added to `WorkflowDefinition` record with `[Id(5)]`.

## Application Layer

### ISignalCorrelationGrain

```csharp
public interface ISignalCorrelationGrain : IGrainWithStringKey
{
    ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId);
    ValueTask Unsubscribe(Guid workflowInstanceId, string activityId);
    ValueTask<int> BroadcastSignal();
}
```

### SignalCorrelationGrain

- Keyed by signal name
- `Subscribe`: adds `SignalSubscription` to list, persists state
- `Unsubscribe`: removes matching `(workflowInstanceId, activityId)` from list, persists
- `BroadcastSignal`: snapshots and clears all subscriptions, persists state, then delivers to each workflow via `Task.WhenAll`. Returns count of successful deliveries. Per-subscriber errors are logged but don't block other deliveries.
- Uses `[PersistentState("state", GrainStorageNames.SignalCorrelations)]`
- LoggerMessage EventId range: 9100-9199

### IWorkflowInstanceGrain additions

```csharp
[AlwaysInterleave]
Task HandleSignalDelivery(string activityId, Guid hostActivityInstanceId);

[AlwaysInterleave]
Task HandleBoundarySignalFired(string boundaryActivityId, Guid hostActivityInstanceId);
```

`[AlwaysInterleave]` is required because a workflow's throw event can trigger delivery back to the same workflow (self-signaling) or the signal grain may deliver to multiple workflows including the caller.

### WorkflowInstance implementation

**`RegisterSignalSubscription`**: looks up `SignalDefinition` from `definition.Signals`, gets `ISignalCorrelationGrain(signalDef.Name)`, calls `Subscribe`.

**`RegisterBoundarySignalSubscription`**: same pattern but for boundary events.

**`ThrowSignal`**: looks up `SignalDefinition`, gets `ISignalCorrelationGrain(signalDef.Name)`, calls `BroadcastSignal`.

**`HandleSignalDelivery`**: same pattern as `HandleMessageDelivery` but with empty `ExpandoObject` (signals carry no data). Dispatches to boundary handler if activity is `SignalBoundaryEvent`.

### BoundaryEventHandler additions

**`HandleBoundarySignalFiredAsync`**: interrupts host, unsubscribes timer/message/other-signal boundaries, creates and executes boundary instance.

**`UnsubscribeBoundarySignalSubscriptionsAsync`**: iterates `SignalBoundaryEvent` activities attached to host, unsubscribes each from its signal grain. Takes optional `skipSignalName` to avoid deadlock when called from within the signal grain's delivery.

**Cross-boundary cleanup**: When any boundary fires (timer, message, or signal), ALL other boundary subscriptions for that host must be cleaned up:
- `HandleBoundaryTimerFiredAsync` → also unsubscribe signal boundaries
- `HandleBoundaryMessageFiredAsync` → also unsubscribe signal boundaries
- `HandleBoundarySignalFiredAsync` → unsubscribe timer + message + other signal boundaries

### BoundarableActivity additions

Register `SignalBoundaryEvent` subscriptions in `RegisterBoundaryEventsAsync`, alongside timer and message boundaries.

## Infrastructure Layer

### BpmnConverter additions

**Parse `<signal>` definitions** at `<definitions>` level (new `ParseSignals` method):
```xml
<signal id="Signal_1" name="order-approved" />
```
Creates `SignalDefinition(id, name)`.

**Parse intermediate catch events**: extend existing `<intermediateCatchEvent>` parsing to check for `<signalEventDefinition signalRef="...">`.

**Parse intermediate throw events**: new section for `<intermediateThrowEvent>` with `<signalEventDefinition signalRef="...">`.

**Parse boundary events**: extend existing `<boundaryEvent>` parsing to check for `<signalEventDefinition signalRef="...">`.

## API Layer

New endpoint on `WorkflowController`:

```
POST /signal
Body: { "SignalName": "order-approved" }
Response: { "DeliveredCount": 3 }
```

`SendSignalRequest` and `SendSignalResponse` DTOs in `Fleans.ServiceDefaults`.

## Persistence Layer

- `GrainStorageNames.SignalCorrelations = "signalCorrelations"`
- `EfCoreSignalCorrelationGrainStorage` (mirrors `EfCoreMessageCorrelationGrainStorage`)
- `SignalCorrelations` DbSet on `FleanCommandDbContext`
- `SignalCorrelations` table in `FleanModelConfiguration`
- Register in `AddEfCorePersistence`

## Design Decisions

1. **Parallel delivery with error isolation**: `BroadcastSignal` uses `Task.WhenAll` so one subscriber failure doesn't block others. Failed deliveries are logged.

2. **At-most-once semantics**: Subscriptions are removed before delivery (same as messages). A subscriber that fails delivery won't receive the signal again.

3. **`[AlwaysInterleave]` on signal handlers**: Required for reentrancy when a workflow signals itself or when the signal grain calls back into the workflow.

4. **No payload on signals**: BPMN spec says signals are pure notifications. Catch events complete with empty `ExpandoObject`.

5. **List (not dict) for subscriptions**: Multiple activities in the same workflow can subscribe to the same signal. Unsubscribe matches by `(workflowInstanceId, activityId)` pair.

## Test Plan

Tests in `Fleans.Domain.Tests/` using Orleans TestCluster:

1. **Signal catch + external throw**: Workflow with catch event, external API throws signal, workflow resumes
2. **Signal catch + workflow throw**: Workflow A throws signal, Workflow B catches it and resumes
3. **Self-signaling**: Workflow throws signal that it also catches (reentrancy test)
4. **Multiple subscribers**: Two workflows waiting for same signal, both receive it
5. **Signal boundary event**: Signal fires, interrupts host activity, boundary path executes
6. **Boundary cleanup**: Signal boundary fires, timer/message boundaries for same host are unsubscribed
7. **Stale boundary**: Signal fires after host already completed → ignored
8. **Throw event completion**: Throw event completes and workflow proceeds to next activity
9. **BPMN parsing**: Signal definitions, catch, throw, and boundary events parsed correctly from XML
