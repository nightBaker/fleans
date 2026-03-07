# Signal Start Event Design

## Problem

Workflows can only be started via explicit API call or message start events. BPMN signal start events allow broadcast-triggered process instantiation — a signal is fired and all subscribed process definitions automatically get new workflow instances created and started.

Unlike messages (point-to-point with correlation), signals are broadcast. A signal should fan out to both running instances (via existing `SignalCorrelationGrain.BroadcastSignal`) AND create new instances (via new `SignalStartEventListenerGrain`). Both always execute independently.

## Design

### Domain: `SignalStartEvent` Activity

Record extending `Activity` with `SignalDefinitionId` property. Identical execution pattern to `TimerStartEvent` and `MessageStartEvent` — completes immediately on `ExecuteAsync`, returns outgoing sequence flow from `GetNextActivities`.

### State: `SignalStartEventListenerState`

Same shape as `MessageStartEventListenerState`: `Key` (signal name), `ETag`, `List<string> ProcessDefinitionKeys`. The grain holds process definition keys (not IDs) because `GetLatestWorkflowDefinition` requires the stable key.

### Persistence: Relational Join Tables

Replace JSON-serialized `List<string>` columns with proper join tables for both message and signal start event listeners:

- `MessageStartEventRegistrations` table: `MessageName` (FK to `MessageStartEventListeners.Key`) + `ProcessDefinitionKey` composite PK. Replaces JSON column on `MessageStartEventListeners`.
- `SignalStartEventRegistrations` table: `SignalName` (FK to `SignalStartEventListeners.Key`) + `ProcessDefinitionKey` composite PK.

Grain storage adapters read rows into the in-memory list and write by diffing rows (add new, remove missing). The grain code (`State.ProcessDefinitionKeys`) stays unchanged — only the persistence layer changes.

`MessageCorrelationState` and `SignalCorrelationState` already use separate relational child tables (`MessageSubscriptions`, `SignalSubscriptions`) — no changes needed for those.

### Grain: `ISignalStartEventListenerGrain`

`IGrainWithStringKey` keyed by signal name.

- `RegisterProcess(string processDefinitionKey)` — called at deploy time
- `UnregisterProcess(string processDefinitionKey)` — called on redeployment when signal is removed
- `FireSignalStartEvent()` → `List<Guid>` — creates workflow instances with empty variables, returns instance IDs. Each instantiation is wrapped in try-catch so one failure doesn't block others.

Uses `FindSignalStartActivityId(definition, signalName)` to pass the correct `startActivityId` to `SetWorkflow`, ensuring the right start activity is selected when a process has multiple start event types.

### BPMN Parsing

In `BpmnConverter.ParseActivities`, inside the `<startEvent>` loop — after existing `timerEventDefinition` and `messageEventDefinition` checks, add `signalEventDefinition` check. Extract `signalRef` attribute, create `SignalStartEvent(id, signalRef)`.

### Deployment Integration

In `WorkflowInstanceFactoryGrain.DeployWorkflow`:
- Scan for `SignalStartEvent` activities
- Look up signal name from `definition.Signals` by `SignalDefinitionId`
- Call `ISignalStartEventListenerGrain(signalName).RegisterProcess(processDefinitionKey)`
- On redeployment: unregister signal names removed from the new version (same pattern as message start events)

### API: Fan-Out Delivery

Move `SendSignal` orchestration from `WorkflowController` to `WorkflowCommandService.SendSignal`:

```
1. BroadcastSignal() → delivers to running instances (returns count)
2. FireSignalStartEvent() → creates new instances (returns List<Guid>)
Both always execute. Independent — one failing doesn't block the other.
```

`SendSignalResponse`: add `List<Guid>? WorkflowInstanceIds = null`.

Return 404 only if both deliveredCount == 0 AND no instances created.

### SetWorkflow Update

Add `SignalStartEvent` to the auto-detect pattern match in `WorkflowInstance.SetWorkflow`:
```csharp
a is StartEvent or TimerStartEvent or MessageStartEvent or SignalStartEvent
```

## Key Differences from Message Start Event

| Aspect | Message Start Event | Signal Start Event |
|--------|--------------------|--------------------|
| Payload | Carries `ExpandoObject variables` | No payload (empty variables) |
| Delivery | Fallthrough: correlation first, then start event | Fan-out: broadcast AND start event, both always execute |
| Correlation | Has correlation key for routing to running instances | No correlation — broadcast to all |
