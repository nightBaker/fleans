# Workflow Execution Domain Aggregate — Design

**Date:** 2026-03-10
**Type:** Refactoring design (P0 from DDD architecture audit)
**Status:** Approved
**Addresses:** "Core BPMN logic lives in the grain layer" + "Anemic domain model"

---

## Summary

Extract all BPMN orchestration logic from the `WorkflowInstance` grain into a `WorkflowExecution` domain aggregate in `Fleans.Domain`. Eliminate the `ActivityInstance` grain by folding activity state into `WorkflowInstanceState`. The grain becomes a thin coordinator that calls the aggregate and performs infrastructure effects.

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

## WorkflowExecution Domain Aggregate

### Location

`Fleans.Domain/Aggregates/WorkflowExecution.cs`

### Responsibility

Owns `WorkflowInstanceState` and encapsulates all BPMN orchestration logic. Every method is synchronous — operates on local state and returns infrastructure effects.

### API

```csharp
public class WorkflowExecution
{
    private readonly WorkflowInstanceState _state;
    private readonly IWorkflowDefinition _definition;

    // Workflow lifecycle
    ExecutionResult Start();

    // Activity execution feedback
    ExecutionResult ProcessCommands(IReadOnlyList<IExecutionCommand> commands, Guid activityInstanceId);
    ExecutionResult ResolveTransitions();

    // External event entry points
    ExecutionResult CompleteActivity(string activityId, Guid? activityInstanceId, ExpandoObject variables);
    ExecutionResult FailActivity(string activityId, Guid? activityInstanceId, Exception exception);
    ExecutionResult HandleTimerFired(string timerActivityId, Guid hostActivityInstanceId);
    ExecutionResult HandleMessageDelivery(string activityId, Guid hostActivityInstanceId, ExpandoObject variables);
    ExecutionResult HandleSignalDelivery(string activityId, Guid hostActivityInstanceId);
    ExecutionResult CompleteConditionSequence(string activityId, Guid activityInstanceId, string sequenceId, bool result);
    ExecutionResult OnChildWorkflowCompleted(string parentActivityId, ExpandoObject variables);
    ExecutionResult OnChildWorkflowFailed(string parentActivityId, Exception exception);

    // Queries
    IReadOnlyList<PendingActivity> GetPendingActivities();
    void MarkExecuting(Guid activityInstanceId);
}
```

### ExecutionResult

```csharp
public record ExecutionResult(
    IReadOnlyList<IInfrastructureEffect> Effects,
    IReadOnlyList<IDomainEvent> Events);
```

The aggregate mutates `WorkflowInstanceState` internally, then returns effects the grain must perform and events to publish. The grain never touches state directly.

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

The aggregate returns declarative effects. The grain performs them.

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

State mutations are NOT effects. The aggregate applies those directly to `WorkflowInstanceState` before returning. The grain just calls `WriteStateAsync()` after performing effects.

### Mapping

The existing `IExecutionCommand` types that activities return from `ExecuteAsync` get translated by `ProcessCommands()` into infrastructure effects. Commands remain as the activity-to-aggregate contract; effects are the aggregate-to-grain contract.

---

## Grain as Thin Coordinator

The `WorkflowInstance` grain shrinks to a coordinator loop.

```csharp
public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain
{
    private WorkflowExecution _execution;

    public async Task StartWorkflow()
    {
        var result = _execution.Start();
        await PerformEffects(result);
        await RunExecutionLoop();
        await _state.WriteStateAsync();
    }

    public async Task CompleteActivity(string activityId, Guid activityInstanceId,
        ExpandoObject variables)
    {
        var result = _execution.CompleteActivity(activityId, activityInstanceId, variables);
        await PerformEffects(result);
        await RunExecutionLoop();
        await _state.WriteStateAsync();
    }

    // All entry points follow the same 4-step pattern:
    // 1. Call aggregate method -> get result
    // 2. PerformEffects(result)
    // 3. RunExecutionLoop()
    // 4. WriteStateAsync()

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
                var result = _execution.ProcessCommands(commands, p.ActivityInstanceId);
                await PerformEffects(result);
            }

            var transitions = _execution.ResolveTransitions();
            await PerformEffects(transitions);
        }
    }

    private async Task PerformEffects(ExecutionResult result)
    {
        foreach (var effect in result.Effects)
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
                // ... one case per effect type
            }
        }
        foreach (var evt in result.Events)
            await _eventPublisher.Publish(evt);
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
    public ValueTask<Guid> GetVariablesStateId() => ValueTask.FromResult(_execution.GetVariablesId(_activityInstanceId));
    // ... all methods delegate to aggregate
}
```

### IWorkflowExecutionContext

The grain still implements `IWorkflowExecutionContext` but delegates reads to the aggregate's state. Activities don't know the difference.

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
- `WorkflowInstance.Logging.cs` — logging moves to aggregate (using injected ILogger)

### New

- `WorkflowExecution` aggregate in `Fleans.Domain`
- `ActivityExecutionContextAdapter` in `Fleans.Application`
- Infrastructure effect records in `Fleans.Domain`
- `ExecutionResult` record in `Fleans.Domain`

### Unchanged

- All `Activity` subclasses — same `IExecutionCommand` return, same interfaces
- `IExecutionCommand` types
- `IWorkflowDefinition`, `WorkflowDefinition`, `SubProcess`
- External grains: `TimerCallbackGrain`, `MessageCorrelationGrain`, `SignalCorrelationGrain`, `WorkflowEventsPublisher`, script/condition handlers
- `IWorkflowInstanceGrain` interface (same external contract)
- `WorkflowInstanceFactoryGrain`
- All integration tests (same external behavior, same grain interface)

### Testing Improvement

`WorkflowExecution` is a plain C# class — testable without Orleans `TestCluster`. Unit tests construct it with `WorkflowInstanceState` + `IWorkflowDefinition`, call methods, assert on returned effects and state mutations. Integration tests remain unchanged.

---

## EF Core Persistence Impact

The `Fleans.Persistence` layer currently stores `ActivityInstanceState` as separate EF Core entities. With this change:

- `ActivityInstanceEntity` table merges into an `ActivityInstanceEntries` collection on `WorkflowInstanceEntity` (or a related table with FK to workflow instance)
- The `WorkflowQueryService` read queries update to join the merged structure
- Migration required for existing data (if any)
