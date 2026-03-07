# Message Start Event Design

## Problem

Message start events are not supported. Currently, workflows can only be started explicitly via `POST /Workflow/start`. In BPMN, a message start event allows a workflow to be instantiated automatically when a matching message arrives — enabling event-driven process initiation.

## Solution

Add a `MessageStartEvent` activity and a `MessageStartEventListenerGrain` that listens for messages and creates new workflow instances on arrival. Integrate with the existing unified `POST /Workflow/message` endpoint using a fallthrough pattern: try correlation (running instance) first, then start event listener.

## Domain Model

### MessageStartEvent

New activity class in `Fleans.Domain/Activities/`, extending `Activity`:

```csharp
public record MessageStartEvent(string ActivityId, string MessageDefinitionId) : Activity(ActivityId)
{
    public override Task<List<ExecutionCommand>> ExecuteAsync(...)
    {
        await activityInstance.Complete();
        return [];
    }

    public override List<SequenceFlow> GetNextActivities(...) => /* outgoing flow */;
}
```

Behavior mirrors `TimerStartEvent` — the event has already fired by the time the workflow starts, so `ExecuteAsync()` immediately completes.

### BPMN Parsing

In `BpmnConverter.ParseActivities()`, add a check for `<messageEventDefinition>` inside `<startEvent>` elements (alongside the existing `<timerEventDefinition>` check). Extract `messageRef` attribute, create `MessageStartEvent`.

No correlation key is needed for message start events — the `MessageDefinition.CorrelationKeyExpression` stays null.

## Grain Architecture

### MessageStartEventListenerGrain

Keyed by `messageName` (string). Supports multiple process definitions subscribing to the same message name.

```csharp
public interface IMessageStartEventListenerGrain : IGrainWithStringKey
{
    ValueTask RegisterProcess(string processDefinitionId);
    ValueTask UnregisterProcess(string processDefinitionId);
    ValueTask<List<Guid>> FireMessageStartEvent(ExpandoObject variables);
}
```

**State:** `MessageStartEventListenerState` — stores a `List<string> ProcessDefinitionIds`.

**Behavior:**
- `RegisterProcess(processDefinitionId)`: called during deployment. Adds the process definition ID to the list. Idempotent — if already registered, replaces (handles redeployment of same process).
- `UnregisterProcess(processDefinitionId)`: removes a specific process definition from the list. Called when a process is redeployed without a message start event for this message name. If the list becomes empty, clears state.
- `FireMessageStartEvent(variables)`: creates a new workflow instance for **each** registered process definition. Sets initial variables from the message payload, starts each workflow. Returns the list of new instance IDs.

### Why a list of registrations?

Multiple process definitions can legitimately listen to the same message name. For example, an `"orderReceived"` message might trigger both an `order-fulfillment` process and an `order-analytics` process. The grain must fan out to all registered processes, not just the last one deployed.

### Deployment Integration

In `WorkflowInstanceFactoryGrain.DeployWorkflow()`, after the existing timer start event check:

```csharp
var messageStartEvent = definition.Activities.OfType<MessageStartEvent>().FirstOrDefault();
if (messageStartEvent is not null)
{
    var messageDef = definition.Messages.First(m => m.Id == messageStartEvent.MessageDefinitionId);
    var listener = GrainFactory.GetGrain<IMessageStartEventListenerGrain>(messageDef.Name);
    await listener.RegisterProcess(processDefinitionId);
}
```

When redeploying a process that previously had a message start event but no longer does, the factory should call `UnregisterProcess()` for the old message name. This requires tracking the previous deployment's message start event — store the previous `messageName` (if any) in the deployment flow and unregister if it changed or was removed.

## API & Delivery Flow

### CorrelationKey becomes optional

The current `SendMessageRequest` requires `CorrelationKey` (controller returns BadRequest if empty). This must change: `CorrelationKey` becomes optional. Message start events don't use correlation keys.

```csharp
// Before: CorrelationKey is required
// After:
record SendMessageRequest(string MessageName, string? CorrelationKey, object? Variables);
```

Remove the `string.IsNullOrWhiteSpace(request.CorrelationKey)` validation from the controller.

### Unified delivery flow

`POST /Workflow/message` with fallthrough logic:

1. If `CorrelationKey` is provided and non-empty → try `MessageCorrelationGrain` (existing correlation delivery)
2. If delivered → return 200 `{ Delivered: true }`
3. If not delivered (no subscription) OR no correlation key → fall through to `MessageStartEventListenerGrain(messageName)`
4. Call `FireMessageStartEvent(variables)`
5. If listener fires (returns non-empty list) → return 200 `{ Delivered: true, WorkflowInstanceIds: [...] }`
6. If no listener or empty list → return 404

The two-step fallthrough (correlation grain → listener grain) is not atomic, but this is acceptable: both steps are sequential calls from the controller, and Orleans' single-threaded grain model ensures each individual grain call is consistent. The window between the two calls is negligible in practice.

### Response Extension

```csharp
record SendMessageResponse(bool Delivered, List<Guid>? WorkflowInstanceIds = null);
```

- Catch event delivery: `WorkflowInstanceIds` is null (caller already knows the instance).
- Start event creation: returns the list of new instance IDs (one per registered process).

## Semantics

### At-least-once delivery

Message start events have **at-least-once** semantics, consistent with the correlation grain. If a message is delivered twice (network retry, duplicate send), two sets of instances are created. This is by design — the listener grain is stateless with respect to individual messages (it doesn't track which messages it has seen).

Callers that need exactly-once instance creation should implement deduplication at a higher level (e.g., application-level idempotency key). This is a future enhancement, not part of this design.

## Testing

### Unit Tests
- `MessageStartEvent`: completes immediately, returns next activities
- BPMN converter: parses `<startEvent><messageEventDefinition>` correctly

### Integration Tests (Orleans TestCluster)
- Deploy workflow with message start event → send message → new instance created and running
- Message variables propagated as initial workflow variables
- Message with no matching listener → 404
- Two workflows with different message start events for different messages → correct routing
- Two workflows with same message name → both instantiated on single message
- Redeployment: redeploy without message start event → old listener unregistered
- Fallthrough: message with correlation key delivered to running instance, not start event

### Manual Test
- BPMN fixture: `<startEvent><messageEventDefinition>` → `<scriptTask>` → `<endEvent>`
- Deploy, send message via API, verify instance created and completed

## Design Decisions

**Why a separate listener grain instead of extending MessageCorrelationGrain?** The correlation grain is partitioned by `messageName/correlationKey` and holds exactly one subscription to a running instance. Start events have no correlation key and create new instances — fundamentally different semantics. Separate grains keep concerns clean.

**Why a list of process definitions per grain?** Multiple processes can legitimately listen to the same message name. A single-registration model would silently overwrite previous deployments — a data-loss bug.

**Why no correlation key for start events?** Standard BPMN behavior. A message start event listens for a message name, not a specific correlation value. Any matching message creates a new instance.

**Why unified API endpoint?** Callers shouldn't need to know whether a message triggers a running instance or creates a new one. The engine determines the correct behavior based on deployed definitions.

**Why no DeactivateListener()?** There's no undeploy mechanism in the codebase. `UnregisterProcess()` handles the case where a redeployed process no longer has a message start event. A blanket deactivate method would be dead code — YAGNI.

**Why no deduplication?** At-least-once semantics are consistent with the correlation grain design. Adding message-level deduplication (e.g., storing recent message IDs) adds complexity and state management without clear demand. If needed, it can be layered on top.

## Files to Create/Modify

**Create:**
- `src/Fleans/Fleans.Domain/Activities/MessageStartEvent.cs`
- `src/Fleans/Fleans.Application/Grains/IMessageStartEventListenerGrain.cs`
- `src/Fleans/Fleans.Application/Grains/MessageStartEventListenerGrain.cs`
- `src/Fleans/Fleans.Domain/States/MessageStartEventListenerState.cs`

**Modify:**
- `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs` — parse message start events
- `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs` — register listener on deploy
- `src/Fleans/Fleans.Api/Controllers/WorkflowController.cs` — make CorrelationKey optional, add fallthrough logic
- `src/Fleans/Fleans.ServiceDefaults/DTOs/` — update `SendMessageRequest` and `SendMessageResponse`
