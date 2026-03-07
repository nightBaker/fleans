# Start Event Unregistration on Redeployment & Process Disable/Enable

**Date:** 2026-03-07
**Status:** Approved

## Problem

1. **Timer start event leak:** When a new workflow version removes a `TimerStartEvent`, the old Orleans reminder keeps firing indefinitely. The `DeactivateScheduler()` method exists but is never called during redeployment.
2. **No undeploy mechanism:** There is no way to disable a deployed process definition without deploying a new version. Start event listeners (timer, message, signal) cannot be stopped without restarting the silo.

Message and signal start event unregistration on redeployment works correctly — only the timer case is broken.

## Design

### 1. Timer Deactivation Bug Fix

In `WorkflowInstanceFactoryGrain.DeployWorkflow()`, add an `else` branch after the existing timer activation block:

```csharp
if (workflowWithId.Activities.OfType<TimerStartEvent>().Any())
{
    var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
    await scheduler.ActivateScheduler(processDefinitionId);
}
else if (versions.Count > 1 && previousWorkflow.Activities.OfType<TimerStartEvent>().Any())
{
    var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
    await scheduler.DeactivateScheduler();
}
```

This mirrors the message/signal unregistration pattern.

### 2. Domain Model: IsActive Flag

Add `bool IsActive` to `ProcessDefinition` (defaults to `true` on deployment). Persisted as a column on the `ProcessDefinitions` table via `FleanModelConfiguration`.

### 3. Disable Process

`IWorkflowInstanceFactoryGrain.DisableProcess(string processDefinitionKey)`:

1. Validate the process key exists
2. Set `IsActive = false`
3. Unregister all start event listeners from the latest version:
   - Timer: `scheduler.DeactivateScheduler()`
   - Messages: for each `MessageStartEvent`, `listener.UnregisterProcess(processDefinitionKey)`
   - Signals: for each `SignalStartEvent`, `listener.UnregisterProcess(processDefinitionKey)`
4. Persist state

### 4. Enable Process

`IWorkflowInstanceFactoryGrain.EnableProcess(string processDefinitionKey)`:

1. Validate the process key exists and is currently disabled
2. Set `IsActive = true`
3. Re-register all start event listeners from the latest version:
   - Timer: `scheduler.ActivateScheduler(processDefinitionId)`
   - Messages: for each `MessageStartEvent`, `listener.RegisterProcess(processDefinitionKey)`
   - Signals: for each `SignalStartEvent`, `listener.RegisterProcess(processDefinitionKey)`
4. Persist state

### 5. Safety Guards

- `GetLatestWorkflowDefinition` throws if the process is disabled — prevents new instances even if a listener wasn't cleaned up.
- Deploying a new version to a disabled process auto-enables it (sets `IsActive = true` and registers listeners as normal).

### 6. API Endpoints

Add to `WorkflowController`:

- `POST /Workflow/disable` — body: `{"ProcessDefinitionKey": "..."}` — returns updated `ProcessDefinitionSummary`
- `POST /Workflow/enable` — body: `{"ProcessDefinitionKey": "..."}` — returns updated `ProcessDefinitionSummary`

`ProcessDefinitionSummary` gains a `bool IsActive` field.

### 7. Web UI

In the process definition list, add a toggle per process:
- Active processes: "Disable" button
- Disabled processes: "Enable" button, visually dimmed
- Calls grains directly via `WorkflowEngine` (not API, per project conventions)

## Testing

### Integration Tests

1. **Timer deactivation on redeployment** — deploy v1 with `TimerStartEvent`, deploy v2 without, verify scheduler deactivated
2. **Disable process** — deploy with all three start event types, disable, verify all listeners unregistered
3. **Enable process** — disable then re-enable, verify all listeners re-registered
4. **Disabled process blocks instances** — disable, fire start event, verify no instance created
5. **Deploy auto-enables** — disable, deploy new version, verify `IsActive = true` and listeners registered

### Manual Test Plan

Add `tests/manual/NN-start-event-undeploy/` with:
- BPMN fixture with a signal start event
- Steps: deploy, send signal (instance created), disable, send signal (no instance), enable, send signal (instance created again)
