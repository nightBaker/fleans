# Timer Events Design (Phase 1: Items 1.1–1.2)

**Date:** 2026-02-17
**Scope:** Workflow suspension model + Timer Events (all types, all positions)
**Prerequisite for:** Message Events, Signal Events, Event-Based Gateway (future Phase 1 items)

## Context

The current execution model is synchronous — `ExecuteWorkflow()` runs a `while(AnyNotExecuting)` loop, and activities either complete immediately or wait for an external `CompleteActivity()` call. There is no concept of "wait" or "sleep." Timer Events require the engine to schedule future wake-ups.

## Decision: Activity-Level Suspension

No new `IsSuspended` state is needed. The existing `IsExecuting = true` on a timer activity *is* the suspension — the execution loop exits naturally when all active activities are executing. When a reminder fires, it calls `CompleteActivity()`, which re-enters the execution loop. This reuses the existing resume path with minimal state changes.

## Timer Domain Model

### New Activity Types

**`TimerIntermediateCatchEvent(ActivityId, TimerDefinition)`**
- Inline in sequence flow
- Pauses workflow until timer fires

**`BoundaryTimerEvent(ActivityId, AttachedToActivityId, TimerDefinition, IsInterrupting=true)`**
- Attached to an activity
- Fires while the attached activity runs, then interrupts it
- Same structural pattern as existing `BoundaryErrorEvent`

**`TimerStartEvent(ActivityId, TimerDefinition)`**
- Replaces `StartEvent` in a process definition
- Workflow instances are created on a schedule by a separate scheduler grain

### TimerDefinition Value Object

```csharp
record TimerDefinition(TimerType Type, string Expression);

enum TimerType { Duration, Date, Cycle }
```

**Parsing:**
- `timeDuration` → `TimeSpan` via `XmlConvert.ToTimeSpan()` (ISO 8601 duration like `PT5M`)
- `timeDate` → `DateTimeOffset` via `DateTimeOffset.Parse()` (like `2026-03-01T10:00:00Z`)
- `timeCycle` → Parse `R{count}/{duration}` into repeat count + interval (like `R3/PT10M`)

## Timer Execution Flows

### TimerIntermediateCatchEvent

1. `ExecuteAsync()` → calls `base.ExecuteAsync()` (publishes event, marks `IsExecuting = true`)
2. Calls `workflowContext.RegisterReminder(activityId, timerDefinition)` on `WorkflowInstance` grain
3. Execution loop exits naturally (activity is executing, no more work to do)
4. Reminder fires → `IRemindable.ReceiveReminder()` on `WorkflowInstance` → calls `CompleteActivity(activityId)`
5. Execution loop resumes, transitions to next activity

### BoundaryTimerEvent

1. When `ExecuteWorkflow()` processes the attached activity, it also registers the boundary timer's reminder
2. If reminder fires before the activity completes:
   - Cancel/complete the attached activity (interrupting)
   - Complete the boundary timer event
   - Transition to the boundary event's outgoing flow
3. If the attached activity completes first:
   - `UnregisterReminder()` for the boundary timer
   - Boundary timer is discarded

### TimerStartEvent

A separate `TimerStartEventScheduler` grain (keyed by `processDefinitionId`) handles scheduling:

1. When a workflow definition with `TimerStartEvent` is deployed, the scheduler grain activates and registers a reminder
2. When the reminder fires, the scheduler creates a new `WorkflowInstance` grain, sets the definition, and calls `StartWorkflow()`
3. For `timeCycle`: tracks instance count, stops after N fires
4. For `timeDate`: fires once at the specified time
5. For `timeDuration`: fires once after the duration from deployment time
6. Undeploy → scheduler unregisters the reminder

## Orleans Reminders

- **Mechanism:** Orleans Reminders (persistent, survive silo restarts, min ~1 minute granularity)
- **Interface:** `WorkflowInstance` grain implements `IRemindable`
- **Naming:** `"timer:{activityId}"` — unique per activity within the grain
- **Cleanup:** `UnregisterReminder()` called when attached activity completes normally

## BPMN Converter Changes

Parse these elements into new activity types:

| BPMN XML | Domain Type |
|---|---|
| `<intermediateCatchEvent>` with `<timerEventDefinition>` | `TimerIntermediateCatchEvent` |
| `<boundaryEvent>` with `<timerEventDefinition>` | `BoundaryTimerEvent` |
| `<startEvent>` with `<timerEventDefinition>` | `TimerStartEvent` |

Timer definition extracted from child element:
```xml
<timerEventDefinition>
  <timeDuration>PT5M</timeDuration>
  <!-- or <timeDate>2026-03-01T10:00:00Z</timeDate> -->
  <!-- or <timeCycle>R3/PT10M</timeCycle> -->
</timerEventDefinition>
```

## Testing Strategy

Tests use MSTest + Orleans.TestingHost (AAA pattern). Reminder simulation: call `ReceiveReminder()` directly in tests.

### TimerIntermediateCatchEvent Tests

- Workflow reaches timer, simulated reminder fires, workflow resumes and completes
- Timer definition parsing: duration, date, cycle expressions
- Sequential chaining: timer between two tasks
- Variable state preserved across suspension

### BoundaryTimerEvent Tests

- Timer fires before activity completes → interrupts, follows boundary flow
- Activity completes before timer → boundary discarded, normal flow continues
- Reminder cleanup on normal completion

### TimerStartEvent Tests

- Scheduler creates workflow instance on timer fire
- Cycle timer creates N instances then stops
- Scheduler unregisters on undeploy

### BpmnConverter Tests

- XML round-trip for all three timer event types
- Timer definition parsing for duration, date, and cycle types
