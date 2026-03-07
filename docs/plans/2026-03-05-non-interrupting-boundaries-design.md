# Non-Interrupting Boundary Events Design

**Date:** 2026-03-05
**Arch Audit Item:** 4.1 — Non-interrupting boundary events
**Status:** Approved

## Problem

All boundary events (timer, message, signal, error) are currently hardcoded to interrupting behavior: when a boundary fires, the attached activity is cancelled and the boundary path replaces it. BPMN 2.0 supports non-interrupting boundaries where the attached activity continues running while the boundary spawns a parallel branch. This is essential for notification patterns (e.g., "send reminder after 10 minutes while the task is still active").

## Scope

- Add `IsInterrupting` flag to all boundary event types (default `true`)
- Parse BPMN `cancelActivity` attribute in converter
- Non-interrupting handler behavior: skip cancellation, keep other boundaries registered, clone variable scope for parallel branch
- Timer cycle support (`R{count}/{duration}`) for repeating non-interrupting timers
- Fire-once for non-interrupting message and signal boundaries

## Domain Model Changes

### Boundary Event Records

Add `bool IsInterrupting = true` to all 4 boundary event types:

```
BoundaryTimerEvent(ActivityId, AttachedToActivityId, TimerDefinition, IsInterrupting = true)
BoundaryErrorEvent(ActivityId, AttachedToActivityId, ErrorCode?, IsInterrupting = true)
MessageBoundaryEvent(ActivityId, AttachedToActivityId, MessageDefinitionId, IsInterrupting = true)
SignalBoundaryEvent(ActivityId, AttachedToActivityId, SignalDefinitionId, IsInterrupting = true)
```

Error boundaries are always interrupting per BPMN spec. The flag exists for uniformity but the converter always sets it to `true` for error boundaries.

### TimerDefinition

Currently holds `Duration` (ISO 8601 duration string). Add `Cycle` for repeating timers:

```
TimerDefinition(Duration?, Date?, Cycle?)
```

Only one of Duration/Date/Cycle should be set. Cycle format: ISO 8601 repeating interval — `R3/PT10S` (3 times every 10 seconds), `R/PT10S` (infinite until activity completes).

## BPMN Converter Changes

1. Read `cancelActivity` attribute from `<boundaryEvent>` element — default `true` if absent (BPMN spec default)
2. Pass `IsInterrupting = cancelActivity` to boundary event record constructors
3. Parse `<timeCycle>` elements in timer definitions alongside existing `<timeDuration>` and `<timeDate>`
4. Store cycle string in `TimerDefinition.Cycle`

BPMN example:
```xml
<boundaryEvent id="reminder" attachedToRef="longTask" cancelActivity="false">
  <timerEventDefinition>
    <timeCycle>R3/PT10S</timeCycle>
  </timerEventDefinition>
</boundaryEvent>
```

## BoundaryEventHandler Behavior

### Current (interrupting):
```
fire → cancel attached activity → cancel scope children → unregister other boundaries → create boundary instance → transition
```

### Non-interrupting (`IsInterrupting == false`):
```
fire → DO NOT cancel → DO NOT unregister others → clone variable scope → create boundary instance → transition on boundary path
```

### Specific handler changes:

**All boundary handlers** (`HandleBoundaryTimerFiredAsync`, `HandleBoundaryMessageFiredAsync`, `HandleBoundarySignalFiredAsync`):
- Check `boundary.IsInterrupting`
- If `false`: skip `attachedInstance.Cancel()`, skip `CancelScopeChildren()`, skip marking attached entry completed, skip unregistering other boundary subscriptions
- Clone variable scope for boundary branch (same mechanism as parallel gateway fork)
- Create boundary event instance and transition to boundary path

**Stale activity check**: For non-interrupting boundaries, also verify the attached activity is still active. If the activity completed naturally before the boundary fires, silently ignore it.

**Cycle timer re-registration**: After a non-interrupting timer cycle fires:
1. Parse cycle string (e.g., `R3/PT10S`)
2. Decrement repetition count (`R2/PT10S`)
3. Register new `TimerCallbackGrain` with decremented cycle
4. Stop when count reaches 0 or `R0`

## TimerCallbackGrain Cycle Support

**Grain key format** includes iteration counter to avoid collisions:
```
{instanceId}:{hostActivityInstanceId}:{timerActivityId}:{iteration}
```

**Cleanup**: When the attached activity completes (naturally or via interrupting boundary), all remaining cycle timer grains are unregistered via `UnregisterBoundaryReminders`. This method must be updated to find all iteration variants of cycle timers.

## Testing

### Unit tests (MSTest + Orleans TestingHost):

1. **Non-interrupting timer (fire-once)**: Timer fires → boundary path executes → attached activity still active → complete attached naturally → workflow completes via both paths
2. **Non-interrupting timer (cycle R3)**: Timer fires 3 times, each spawning a parallel branch → attached activity completes → remaining timers unregistered
3. **Non-interrupting message**: Message delivered → boundary path runs → attached activity continues → complete attached → workflow completes
4. **Non-interrupting signal**: Signal broadcast → boundary path runs → attached activity continues
5. **Regression: interrupting unchanged**: Existing tests pass (IsInterrupting defaults to `true`)
6. **Edge case: activity completes before boundary**: Non-interrupting boundary silently ignored

### Manual test fixtures:
Add BPMN files and test plans to `tests/manual/15-non-interrupting-boundaries/`.

## Web UI Changes (Fleans.Web)

The management Web UI needs to support `IsInterrupting` and timer cycle for boundary events.

### BPMN Editor (`bpmnEditor.js`)

1. `_extractElementData()` — add `isInterrupting` field. For boundary events, read `bo.cancelActivity` (default `true`).
2. `updateElementProperty()` — add `cancelActivity` case that uses `modeling.updateProperties(element, { cancelActivity: value })`.

### Boundary Event Context Pad (`boundaryEventContextPad.js`)

Currently hardcodes `cancelActivity: true` on line 92. No change needed here — new boundary events default to interrupting, which is correct. The user toggles it via the properties panel after creation.

### Element Properties Panel (`ElementPropertiesPanel.razor`)

1. Add `bool IsInterrupting` property to `BpmnElementData` class (default `true`).
2. Add `isInterrupting` local field and sync in `SyncFromElement()`.
3. Add `FluentCheckbox` for "Interrupting" when element type is `bpmn:BoundaryEvent`. Display before the timer/message/signal sections.
4. On change, call `bpmnEditor.updateElementProperty(elementId, "cancelActivity", value)`.

### BPMN Viewer (`bpmnViewer.js`)

`getElementProperties()` — add `isInterrupting` field. For boundary events, read `bo.cancelActivity` (default `true`).

## Files to Modify

- `Fleans.Domain/Activities/BoundaryTimerEvent.cs` — add `IsInterrupting`
- `Fleans.Domain/Activities/BoundaryErrorEvent.cs` — add `IsInterrupting`
- `Fleans.Domain/Activities/MessageBoundaryEvent.cs` — add `IsInterrupting`
- `Fleans.Domain/Activities/SignalBoundaryEvent.cs` — add `IsInterrupting`
- `Fleans.Domain/Activities/TimerDefinition.cs` — add `Cycle` property
- `Fleans.Infrastructure/Bpmn/BpmnConverter.cs` — parse `cancelActivity` and `<timeCycle>`
- `Fleans.Application/Grains/BoundaryEventHandler.cs` — conditional cancel/continue logic
- `Fleans.Application/Grains/TimerCallbackGrain.cs` — cycle timer re-registration
- `Fleans.Domain.Tests/` — new test classes for non-interrupting boundaries
- `tests/manual/15-non-interrupting-boundaries/` — BPMN fixtures and test plan
- `Fleans.Web/wwwroot/js/bpmnEditor.js` — extract and update `cancelActivity`
- `Fleans.Web/wwwroot/js/bpmnViewer.js` — extract `cancelActivity` for read-only view
- `Fleans.Web/Components/Pages/ElementPropertiesPanel.razor` — IsInterrupting checkbox UI
