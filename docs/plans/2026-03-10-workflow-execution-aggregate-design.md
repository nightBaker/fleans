# Workflow Execution Domain Aggregate — Event-Sourced Design

**Date:** 2026-03-10
**Type:** Refactoring design (P0 from DDD architecture audit)
**Status:** Approved
**Addresses:** "Core BPMN logic lives in the grain layer" + "Anemic domain model"
**Supersedes:** Previous aggregate + effects design (same date)

---

## Summary

Extract all BPMN orchestration logic from the `WorkflowInstance` grain into a `WorkflowExecution` domain aggregate in `Fleans.Domain`. The aggregate uses event sourcing internally: every state mutation is modeled as a named domain event via `Emit(event)` → `Apply(event)`. Eliminate the `ActivityInstance` grain by folding activity state into `WorkflowInstanceState`. The grain becomes a thin coordinator that calls the aggregate, performs infrastructure effects, and persists state snapshots.

---

## Prerequisite: Eliminate ActivityInstance Grain

### Problem

`ActivityInstance` is a separate grain that holds `ActivityInstanceState`. The `WorkflowInstance` grain creates it, calls Execute/Complete/Fail on it, and queries its state — all via remote grain calls. This makes domain logic inherently async and prevents clean extraction.

### Solution

Merge `ActivityInstanceState` fields into `ActivityInstanceEntry` within `WorkflowInstanceState`:

```csharp
public class ActivityInstanceEntry
{
    // Identity
    Guid ActivityInstanceId;
    string ActivityId;
    Guid WorkflowInstanceId;

    // Scoping
    Guid? ScopeId;
    int? MultiInstanceIndex;
    Guid? ChildWorkflowInstanceId;

    // Execution state (moved from ActivityInstanceState)
    string ActivityType;
    bool IsExecuting;
    bool IsCompleted;
    bool IsCancelled;
    string? CancellationReason;
    Guid VariablesId;
    int? ErrorCode;
    string? ErrorMessage;
    Guid? TokenId;
    int? MultiInstanceTotal;

    // Timestamps (moved from ActivityInstanceState)
    DateTimeOffset? CreatedAt;
    DateTimeOffset? ExecutionStartedAt;
    DateTimeOffset? CompletedAt;

    // State transition methods (moved from ActivityInstanceState)
    void Execute();
    void Complete();
    void Fail(Exception exception);
    void Cancel(string reason);
}
```

### Deleted

- `ActivityInstance` grain + `IActivityInstanceGrain` interface
- `ActivityInstanceState` class

### Benefits

- All activity state is local to `WorkflowInstanceState`
- No grain-to-grain calls for state queries
- Atomic persistence (no partial-write inconsistencies)
- Enables synchronous domain aggregate

---

## Domain Event Catalogue

Every state change is a named domain event in `Fleans.Domain/Events/`. These live alongside the existing `IDomainEvent` interface.

### Workflow lifecycle

- `WorkflowStarted(Guid InstanceId, string ProcessDefinitionId)`
- `WorkflowCompleted()`

### Activity lifecycle

- `ActivitySpawned(Guid ActivityInstanceId, string ActivityId, string ActivityType, Guid VariablesId, Guid? ScopeId, int? MultiInstanceIndex, Guid? TokenId)`
- `ActivityExecutionStarted(Guid ActivityInstanceId)`
- `ActivityCompleted(Guid ActivityInstanceId, Guid VariablesId, ExpandoObject Variables)`
- `ActivityFailed(Guid ActivityInstanceId, int ErrorCode, string ErrorMessage)`
- `ActivityCancelled(Guid ActivityInstanceId, string Reason)`

### Variable management

- `VariablesMerged(Guid VariablesId, ExpandoObject Variables)`
- `ChildVariableScopeCreated(Guid ScopeId, Guid ParentScopeId)`
- `VariableScopeCloned(Guid NewScopeId, Guid SourceScopeId)`
- `VariableScopesRemoved(List<Guid> ScopeIds)`

### Gateway/token management

- `ConditionSequencesAdded(Guid GatewayInstanceId, string[] SequenceFlowIds)`
- `ConditionSequenceEvaluated(Guid GatewayInstanceId, string SequenceFlowId, bool Result)`
- `GatewayForkCreated(Guid ForkInstanceId, Guid? ConsumedTokenId)`
- `GatewayForkTokenAdded(Guid ForkInstanceId, Guid TokenId)`
- `GatewayForkRemoved(Guid ForkInstanceId)`

### Parent/child

- `ParentInfoSet(Guid ParentInstanceId, string ParentActivityId)`

Each event has a corresponding handler in the aggregate's `Apply()` method that mutates `WorkflowInstanceState`.

---

## WorkflowExecution Domain Aggregate

### Location

`Fleans.Domain/Aggregates/WorkflowExecution.cs`

### Responsibility

Owns `WorkflowInstanceState` and encapsulates all BPMN orchestration logic. Every state mutation goes through `Emit(event)` → `Apply(event)`. Methods return infrastructure effects for the grain to perform.

### Core Mechanism: Emit/Apply

```csharp
public class WorkflowExecution
{
    private readonly WorkflowInstanceState _state;
    private readonly IWorkflowDefinition _definition;
    private readonly List<IDomainEvent> _uncommittedEvents = new();

    // Emit an event: apply it to state and record it
    private void Emit(IDomainEvent @event)
    {
        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    // Strongly-typed state mutation dispatch
    private void Apply(IDomainEvent @event)
    {
        switch (@event)
        {
            case ActivitySpawned e:
                _state.AddEntry(new ActivityInstanceEntry(
                    e.ActivityInstanceId, e.ActivityId, _state.Id, e.ScopeId) { ... });
                break;
            case ActivityCompleted e:
                _state.GetEntry(e.ActivityInstanceId).Complete();
                _state.MergeState(e.VariablesId, e.Variables);
                break;
            case GatewayForkTokenAdded e:
                _state.FindFork(e.ForkInstanceId).AddToken(e.TokenId);
                break;
            // ... one case per event type
        }
    }

    // Grain reads events for logging/audit
    public IReadOnlyList<IDomainEvent> GetUncommittedEvents() => _uncommittedEvents;
    public void ClearUncommittedEvents() => _uncommittedEvents.Clear();
}
```

### API

Methods return only `IReadOnlyList<IInfrastructureEffect>`. Domain events are accumulated internally and accessed via `GetUncommittedEvents()`.

```csharp
public class WorkflowExecution
{
    // Workflow lifecycle
    IReadOnlyList<IInfrastructureEffect> Start();

    // Activity execution feedback
    IReadOnlyList<IInfrastructureEffect> ProcessCommands(
        IReadOnlyList<IExecutionCommand> commands, Guid activityInstanceId);
    IReadOnlyList<IInfrastructureEffect> ResolveTransitions();

    // External event entry points
    IReadOnlyList<IInfrastructureEffect> CompleteActivity(
        string activityId, Guid? activityInstanceId, ExpandoObject variables);
    IReadOnlyList<IInfrastructureEffect> FailActivity(
        string activityId, Guid? activityInstanceId, Exception exception);
    IReadOnlyList<IInfrastructureEffect> HandleTimerFired(
        string timerActivityId, Guid hostActivityInstanceId);
    IReadOnlyList<IInfrastructureEffect> HandleMessageDelivery(
        string activityId, Guid hostActivityInstanceId, ExpandoObject variables);
    IReadOnlyList<IInfrastructureEffect> HandleSignalDelivery(
        string activityId, Guid hostActivityInstanceId);
    IReadOnlyList<IInfrastructureEffect> CompleteConditionSequence(
        string activityId, Guid activityInstanceId, string sequenceId, bool result);
    IReadOnlyList<IInfrastructureEffect> OnChildWorkflowCompleted(
        string parentActivityId, ExpandoObject variables);
    IReadOnlyList<IInfrastructureEffect> OnChildWorkflowFailed(
        string parentActivityId, Exception exception);

    // Queries
    IReadOnlyList<PendingActivity> GetPendingActivities();
    void MarkExecuting(Guid activityInstanceId);

    // Event access
    IReadOnlyList<IDomainEvent> GetUncommittedEvents();
    void ClearUncommittedEvents();
}
```

### Example: CompleteActivity

```csharp
public IReadOnlyList<IInfrastructureEffect> CompleteActivity(
    string activityId, Guid? activityInstanceId, ExpandoObject variables)
{
    var entry = ResolveEntry(activityId, activityInstanceId);

    // State changes as explicit events
    Emit(new ActivityCompleted(entry.ActivityInstanceId, entry.VariablesId, variables));

    // Domain logic: determine transitions
    var transitions = ResolveNextActivities(entry);
    foreach (var t in transitions)
        Emit(new ActivitySpawned(Guid.NewGuid(), t.ActivityId, ...));

    // Infrastructure: return effects for grain
    return BuildUnsubscribeEffects(activityId, entry);
}
```

### What Moves Into This Aggregate

From the grain partial files:

| Current location | Domain concept |
|---|---|
| `WorkflowInstance.Execution.cs` | Execution loop decisions, transition logic, scope completion, token propagation, multi-instance lifecycle |
| `WorkflowInstance.ActivityLifecycle.cs` | Activity completion/failure, error boundary matching, child workflow handling, condition evaluation |
| `WorkflowInstance.EventHandling.cs` | Timer/message/signal delivery logic (domain decisions only — subscription registration becomes effects) |
| `WorkflowInstance.StateFacade.cs` | State reads (variable lookup, active/completed activities, condition states) |
| `BoundaryEventHandler.cs` | Interrupting/non-interrupting boundary logic, scope cancellation, variable cloning |

---

## Infrastructure Effects Model

The aggregate returns declarative effects. The grain performs them. These are unchanged from the previous design.

```csharp
public interface IInfrastructureEffect { }

// Timer subscriptions
record RegisterTimerEffect(Guid WorkflowInstanceId, Guid HostActivityInstanceId,
    string TimerActivityId, TimeSpan DueTime) : IInfrastructureEffect;
record UnregisterTimerEffect(Guid WorkflowInstanceId, Guid HostActivityInstanceId,
    string TimerActivityId) : IInfrastructureEffect;

// Message subscriptions
record SubscribeMessageEffect(string MessageName, string CorrelationKey,
    Guid WorkflowInstanceId, Guid HostActivityInstanceId) : IInfrastructureEffect;
record UnsubscribeMessageEffect(string MessageName, string CorrelationKey) : IInfrastructureEffect;

// Signal subscriptions
record SubscribeSignalEffect(string SignalName, Guid WorkflowInstanceId,
    Guid HostActivityInstanceId) : IInfrastructureEffect;
record UnsubscribeSignalEffect(string SignalName) : IInfrastructureEffect;
record ThrowSignalEffect(string SignalName) : IInfrastructureEffect;

// Child workflows
record StartChildWorkflowEffect(Guid ChildInstanceId, string ProcessDefinitionKey,
    ExpandoObject InputVariables, string ParentActivityId) : IInfrastructureEffect;

// Parent notifications
record NotifyParentCompletedEffect(Guid ParentInstanceId, string ParentActivityId,
    ExpandoObject Variables) : IInfrastructureEffect;
record NotifyParentFailedEffect(Guid ParentInstanceId, string ParentActivityId,
    Exception Exception) : IInfrastructureEffect;

// Event publishing (script execution, condition evaluation)
record PublishEventEffect(IDomainEvent Event) : IInfrastructureEffect;

// Activity cancellation cleanup
record CancelActivitySubscriptionsEffect(string ActivityId,
    Guid ActivityInstanceId) : IInfrastructureEffect;
```

Infrastructure effects are actions the grain must perform externally. Domain events are state changes applied internally. These are two separate concerns — effects tell the grain what to do; events record what happened to state.

---

## Grain as Thin Coordinator

The grain pattern is nearly identical to the previous design. The only addition is event logging.

```csharp
public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain
{
    private WorkflowExecution _execution;

    public async Task StartWorkflow()
    {
        var effects = _execution.Start();
        await PerformEffects(effects);
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    public async Task CompleteActivity(string activityId, Guid activityInstanceId,
        ExpandoObject variables)
    {
        var effects = _execution.CompleteActivity(activityId, activityInstanceId, variables);
        await PerformEffects(effects);
        await RunExecutionLoop();
        LogAndClearEvents();
        await _state.WriteStateAsync();
    }

    // All entry points follow the same 5-step pattern:
    // 1. Call aggregate method → get effects
    // 2. PerformEffects(effects)
    // 3. RunExecutionLoop()
    // 4. LogAndClearEvents() — log domain events for audit
    // 5. WriteStateAsync()

    private async Task RunExecutionLoop()
    {
        while (true)
        {
            var pending = _execution.GetPendingActivities();
            if (!pending.Any()) break;

            foreach (var p in pending)
            {
                _execution.MarkExecuting(p.ActivityInstanceId);
                var activity = _definition.GetActivity(p.ActivityId);
                var context = new ActivityExecutionContextAdapter(_execution, p.ActivityInstanceId);
                var commands = await activity.ExecuteAsync(this, context, _definition);
                var effects = _execution.ProcessCommands(commands, p.ActivityInstanceId);
                await PerformEffects(effects);
            }

            var transitionEffects = _execution.ResolveTransitions();
            await PerformEffects(transitionEffects);
        }
    }

    private async Task PerformEffects(IReadOnlyList<IInfrastructureEffect> effects)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case RegisterTimerEffect t:
                    var timer = _grainFactory.GetGrain<ITimerCallbackGrain>(...);
                    await timer.Activate(t.DueTime);
                    break;
                case SubscribeMessageEffect m:
                    var corr = _grainFactory.GetGrain<IMessageCorrelationGrain>(...);
                    await corr.Subscribe(...);
                    break;
                case PublishEventEffect e:
                    await _eventPublisher.Publish(e.Event);
                    break;
                // ... one case per effect type
            }
        }
    }

    private void LogAndClearEvents()
    {
        foreach (var evt in _execution.GetUncommittedEvents())
            LogDomainEvent(evt);
        _execution.ClearUncommittedEvents();
    }
}
```

### IActivityExecutionContext Adapter

`ActivityExecutionContextAdapter` replaces the deleted `ActivityInstance` grain. Lives in `Fleans.Application`. Reads/writes `ActivityInstanceEntry` fields through the aggregate.

```csharp
internal class ActivityExecutionContextAdapter : IActivityExecutionContext
{
    private readonly WorkflowExecution _execution;
    private readonly Guid _activityInstanceId;

    public ValueTask Execute() { _execution.MarkExecuting(_activityInstanceId); return default; }
    public ValueTask Complete() { _execution.MarkCompleted(_activityInstanceId); return default; }
    public ValueTask<Guid> GetActivityInstanceId() => ValueTask.FromResult(_activityInstanceId);
    public ValueTask<Guid> GetVariablesStateId()
        => ValueTask.FromResult(_execution.GetVariablesId(_activityInstanceId));
    // ... all methods delegate to aggregate
}
```

### IWorkflowExecutionContext

The grain still implements `IWorkflowExecutionContext` but delegates reads to the aggregate's state. Activities don't know the difference.

---

## Persistence Strategy

### Now: Snapshot-only

Persistence is unchanged from today — `WriteStateAsync()` persists the `WorkflowInstanceState` snapshot via EF Core. Domain events are used internally for clean modeling and logged for audit, but not persisted as events.

### Future: Event store migration

Since all domain events are already defined and emitted, migrating to a full event store is additive:
1. Add event persistence alongside snapshot
2. Optionally rebuild state from events instead of snapshots
3. Enable event replay for debugging/compliance

This migration requires zero changes to the aggregate — just add persistence for `GetUncommittedEvents()`.

---

## Testing

### Event-based assertions (new)

```csharp
var effects = execution.CompleteActivity("task1", instanceId, variables);
var events = execution.GetUncommittedEvents();

Assert.IsInstanceOfType<ActivityCompleted>(events[0]);
Assert.IsInstanceOfType<VariablesMerged>(events[1]);
Assert.IsInstanceOfType<ActivitySpawned>(events[2]);
Assert.AreEqual("task2", ((ActivitySpawned)events[2]).ActivityId);
```

### State-based assertions (still work)

```csharp
execution.CompleteActivity("task1", instanceId, variables);
var entry = state.GetEntry(instanceId);
Assert.IsTrue(entry.IsCompleted);
```

Both styles are valid. Event assertions are more declarative for complex scenarios (parallel gateways, multi-instance, boundary events). No Orleans `TestCluster` needed.

---

## Change Impact

### Deleted

- `ActivityInstance` grain + `IActivityInstanceGrain` interface
- `ActivityInstanceState` class
- `WorkflowInstance.Execution.cs` — logic moves to aggregate
- `WorkflowInstance.ActivityLifecycle.cs` — logic moves to aggregate
- `WorkflowInstance.EventHandling.cs` — logic moves to aggregate
- `WorkflowInstance.StateFacade.cs` — reads move to aggregate
- `BoundaryEventHandler` service + `IBoundaryEventStateAccessor` interface
- `WorkflowInstance.Logging.cs` — logging moves to aggregate

### New

- `WorkflowExecution` aggregate in `Fleans.Domain`
- ~17 domain event records in `Fleans.Domain/Events/`
- Infrastructure effect records in `Fleans.Domain`
- `ActivityExecutionContextAdapter` in `Fleans.Application`

### Unchanged

- All `Activity` subclasses — same `IExecutionCommand` return, same interfaces
- `IExecutionCommand` types
- `IWorkflowDefinition`, `WorkflowDefinition`, `SubProcess`
- External grains: `TimerCallbackGrain`, `MessageCorrelationGrain`, `SignalCorrelationGrain`, `WorkflowEventsPublisher`, script/condition handlers
- `IWorkflowInstanceGrain` interface (same external contract)
- `WorkflowInstanceFactoryGrain`
- All integration tests (same external behavior, same grain interface)

---

## EF Core Persistence Impact

The `Fleans.Persistence` layer currently stores `ActivityInstanceState` as separate EF Core entities. With this change:

- `ActivityInstanceEntity` table merges into an `ActivityInstanceEntries` collection on `WorkflowInstanceEntity` (or a related table with FK to workflow instance)
- `EfCoreActivityInstanceGrainStorage` is deleted
- `EfCoreWorkflowInstanceGrainStorage` diff logic updates for enriched entries
- `WorkflowQueryService` read queries update to join the merged structure
- Migration required for existing data (if any)
