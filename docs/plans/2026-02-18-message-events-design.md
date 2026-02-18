# Message Events Design

**Date:** 2026-02-18
**Audit ref:** Phase 1, items 1.3 + 1.4
**Scope:** Message Intermediate Catch Event + Message Boundary Event with correlation registry

---

## Problem

Workflows need to wait for external correlated messages (e.g., "wait for payment confirmation where orderId=123"). Currently, the only way to resume a waiting workflow is via `CompleteActivity()` with a known `workflowInstanceId` + `activityId`. There is no way for external systems to send a message by business key without knowing workflow internals.

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Correlation registry topology | Per-message-name grain | Natural partition key, avoids singleton bottleneck, barely more complex |
| Correlation key structure | Single string key | Covers 90% of use cases, compound keys can use concatenated strings |
| Event types in scope | Intermediate Catch + Boundary | Same execution pattern as timers. Message Start Event is a different problem (instance creation). |
| Correlation key source | Dynamic from workflow variables | BPMN declares variable name, engine resolves value at catch event activation |
| BPMN portability | Support both Camunda 7 and 8 formats | Parse standard `<message>` + `<messageEventDefinition>`. Support `zeebe:subscription` and `fleans:subscription` for correlation key. Fallback to no-correlation for Camunda 7 style. |

---

## Domain Model

### New Classes

**`MessageDefinition`** — parsed from `<message>` BPMN elements:

```csharp
[GenerateSerializer]
public record MessageDefinition(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string? CorrelationKeyExpression); // workflow variable name, null if not specified
```

**`MessageIntermediateCatchEvent`** — extends `Activity`:

```csharp
[GenerateSerializer]
public record MessageIntermediateCatchEvent(
    string ActivityId,
    [property: Id(1)] string MessageDefinitionId) : Activity(ActivityId);
```

In `ExecuteAsync()`: calls `activityContext.Execute()` only — marks `IsExecuting = true`, does **not** call `Complete()`. The domain layer does not interact with grains. Correlation subscription is handled by `WorkflowInstance` after the execution loop (see Integration section).

**`MessageBoundaryEvent`** — extends `Activity`:

```csharp
[GenerateSerializer]
public record MessageBoundaryEvent(
    string ActivityId,
    [property: Id(1)] string AttachedToActivityId,
    [property: Id(2)] string MessageDefinitionId) : Activity(ActivityId);
```

Same pattern as `BoundaryTimerEvent` — created when message arrives, immediately completes and transitions.

### Changes to Existing Domain

**`WorkflowDefinition` / `IWorkflowDefinition`** — add:

```csharp
[Id(n)] List<MessageDefinition> Messages { get; }
```

Holds all `<message>` definitions parsed from BPMN.

**`IWorkflowExecutionContext`** — add:

```csharp
ValueTask<object?> GetVariable(string variableName);
```

Resolution: iterates `WorkflowInstanceState.VariableStates` in reverse order (last-write-wins). Each `ExpandoObject` is checked via `IDictionary<string, object?>` for the variable name. Returns the first match, or `null` if not found. Used by `WorkflowInstance.RegisterMessageSubscriptions()` to resolve correlation keys.

### No Changes To

- `WorkflowInstanceState` — waiting state already expressed by `IsExecuting=true, IsCompleted=false`. No subscription tracking needed — active subscriptions are derived from definition + current variables at cleanup time.
- `ActivityInstanceState` — same

---

## Correlation Grain

### Interface

```csharp
public interface IMessageCorrelationGrain : IGrainWithStringKey
{
    ValueTask Subscribe(string correlationKey, Guid workflowInstanceId, string activityId);
    ValueTask Unsubscribe(string correlationKey);
    ValueTask<bool> DeliverMessage(string correlationKey, ExpandoObject variables);
}
```

Keyed by **message name** — e.g., `GrainFactory.GetGrain<IMessageCorrelationGrain>("paymentReceived")`.

### State

```csharp
Dictionary<string, MessageSubscription> Subscriptions;
// correlationValue → (workflowInstanceId, activityId)
```

### Behavior

- **`Subscribe`** — called by `WorkflowInstance` when a message catch event activates. Stores mapping.
- **`Unsubscribe`** — called on workflow completion, failure, or when attached activity completes normally (boundary cleanup).
- **`DeliverMessage`** — called by API endpoint. Looks up subscription by correlation key, calls `CompleteActivity()` on target workflow, removes subscription, returns `true`. Returns `false` if no match.

### Edge Cases

- **Duplicate subscription** (same message + correlation key) — reject with exception. Ambiguous routing.
- **Message before subscription** — returns `false`. Sender can retry. Buffering deferred to future work.

### Persistence

Use EF Core grain storage pattern (same as `WorkflowInstance`/`ActivityInstance`). Subscriptions must survive silo restart.

---

## WorkflowInstance Integration

### Message Subscription Registration

Follows the same pattern as `RegisterTimerReminders()`. After `ExecuteWorkflow()` completes its loop and after `WriteStateAsync()`, a new `RegisterMessageSubscriptions()` method runs:

```
private async Task RegisterMessageSubscriptions()
{
    for each active MessageIntermediateCatchEvent in definition:
        if activityInstance.IsExecuting and not IsCompleted:
            resolve correlationValue = GetVariable(correlationKeyExpression)
            call MessageCorrelationGrain(messageName).Subscribe(correlationValue, workflowInstanceId, activityId)

    for each MessageBoundaryEvent in definition:
        if attached activity is active and executing:
            resolve correlationValue = GetVariable(correlationKeyExpression)
            call MessageCorrelationGrain(messageName).Subscribe(correlationValue, workflowInstanceId, activityId)
}
```

**Key: subscription registration happens after `WriteStateAsync()`** — the workflow state is persisted before any external grain calls. If the silo crashes after persist but before subscribe, the workflow recovers and re-registers on next activation.

### Message Delivery

The correlation grain calls the existing `CompleteActivity(activityId, variables)` on the target `IWorkflowInstanceGrain`. No new method needed — `CompleteActivity` already marks the activity completed, merges variables, and resumes `ExecuteWorkflow()`.

For boundary message events, the correlation grain calls a new `HandleBoundaryMessageFired(activityId)` method (same pattern as `HandleBoundaryTimerFired`):

1. Cancels the attached activity
2. Creates `MessageBoundaryEvent` activity instance
3. Boundary event immediately completes, transitions to outgoing flow

### Cleanup (Unsubscribe)

No subscription tracking on `WorkflowInstanceState`. Active subscriptions are derived from definition + current variables:

```
private async Task UnsubscribeMessageSubscriptions()
{
    for each MessageIntermediateCatchEvent in definition:
        resolve correlationValue = GetVariable(correlationKeyExpression)
        call MessageCorrelationGrain(messageName).Unsubscribe(correlationValue)

    for each MessageBoundaryEvent in definition:
        resolve correlationValue = GetVariable(correlationKeyExpression)
        call MessageCorrelationGrain(messageName).Unsubscribe(correlationValue)
}
```

Called when:
- Workflow completes or fails
- Attached activity completes normally (boundary cleanup, within `CompleteActivityState`)

The correlation key variable is stable while the catch event waits — no activity is completing and merging new variables, so the resolved value at unsubscribe time equals the value at subscribe time.

---

## BPMN Parsing

### BpmnConverter Changes

1. **Parse `<message>` elements** at `<definitions>` level — extract `id`, `name`, and correlation key from extension elements.

2. **Parse `<intermediateCatchEvent>` with `<messageEventDefinition>`** — resolve referenced `MessageDefinition`, create `MessageIntermediateCatchEvent`.

3. **Parse `<boundaryEvent>` with `<messageEventDefinition>`** — extract `attachedToRef` + message reference, create `MessageBoundaryEvent`.

### Correlation Key Namespace Resolution

Check in order:
1. `zeebe:subscription[@correlationKey]` — Camunda 8 portability
2. `fleans:subscription[@correlationKey]` — our namespace
3. If neither present — `CorrelationKeyExpression` is `null` (Camunda 7 style, caller provides correlation at send time)

### BPMN XML Examples

**Camunda 8 style:**
```xml
<definitions xmlns:zeebe="http://camunda.org/schema/zeebe/1.0">
  <message id="msg_payment" name="paymentReceived">
    <extensionElements>
      <zeebe:subscription correlationKey="= orderId" />
    </extensionElements>
  </message>
  <process id="orderProcess">
    <intermediateCatchEvent id="waitForPayment">
      <messageEventDefinition messageRef="msg_payment" />
    </intermediateCatchEvent>
  </process>
</definitions>
```

**Camunda 7 style (no correlation in XML):**
```xml
<definitions>
  <message id="msg_payment" name="paymentReceived" />
  <process id="orderProcess">
    <intermediateCatchEvent id="waitForPayment">
      <messageEventDefinition messageRef="msg_payment" />
    </intermediateCatchEvent>
  </process>
</definitions>
```

---

## API Endpoint

**New endpoint on `WorkflowController`:**

```
POST /api/workflow/message
{
  "messageName": "paymentReceived",
  "correlationKey": "order-123",
  "variables": { "amount": 150.00 }
}
```

- Gets `IMessageCorrelationGrain(messageName)`, calls `DeliverMessage(correlationKey, variables)`
- Returns `200 OK` if delivered
- Returns `404` if no matching subscription

---

## Testing

### Integration Tests (Orleans TestCluster)

1. **Happy path** — start → message catch → end. Start workflow, verify waiting. Send message with correct key. Verify completed, variables merged.
2. **Wrong correlation key** — send non-matching key. Verify returns false, workflow still waiting.
3. **Boundary message event** — start → task (with message boundary) → end. Send message while task active. Verify task cancelled, boundary path taken.
4. **Boundary not triggered** — complete task normally. Verify boundary subscription cleaned up.

### Domain Tests

- `MessageIntermediateCatchEvent` and `MessageBoundaryEvent` — `GetNextActivities()` tests, following `TimerIntermediateCatchEventDomainTests` pattern.

### BpmnConverter Tests

- Parse BPMN XML with message events — verify correct domain objects.
- Test both `zeebe:subscription` and `fleans:subscription` namespaces.
- Test Camunda 7 style (no correlation key) — `CorrelationKeyExpression` is null.

---

## Out of Scope

- Message Start Event (creates new workflow instances — different problem)
- Message Throw Event (sends message from workflow to another workflow)
- Message buffering (message arrives before subscription)
- Multi-property correlation keys
- FEEL expression evaluation (we just read variable name, strip `=` prefix)
