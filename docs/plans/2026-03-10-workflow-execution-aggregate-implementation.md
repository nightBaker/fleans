# Event-Sourced WorkflowExecution Aggregate — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extract all BPMN orchestration logic from the WorkflowInstance grain into an event-sourced WorkflowExecution domain aggregate, eliminating the ActivityInstance grain.

**Architecture:** The aggregate uses Emit/Apply internally — every state mutation is a named domain event. Methods return infrastructure effects for the grain to perform. Persistence remains snapshot-based via EF Core; domain events provide audit trail and clean modeling. The grain becomes a thin coordinator loop.

**Tech Stack:** C# / .NET 10, Orleans 10, EF Core, MSTest, NSubstitute

**Design doc:** `docs/plans/2026-03-10-workflow-execution-aggregate-design.md`

---

## Phase 1: Foundation Types

### Task 1: Enrich ActivityInstanceEntry with execution state

**Files:**
- Modify: `src/Fleans/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/ActivityInstanceEntryTests.cs`

**Step 1: Write failing tests for new state transition methods**

```csharp
// ActivityInstanceEntryTests.cs
using Fleans.Domain.Errors;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class ActivityInstanceEntryTests
{
    private ActivityInstanceEntry CreateEntry(string activityId = "task1")
        => new(Guid.NewGuid(), activityId, Guid.NewGuid());

    [TestMethod]
    public void Execute_ShouldSetIsExecutingAndTimestamp()
    {
        var entry = CreateEntry();
        entry.Execute();
        Assert.IsTrue(entry.IsExecuting);
        Assert.IsNotNull(entry.ExecutionStartedAt);
    }

    [TestMethod]
    public void Execute_WhenAlreadyExecuting_ShouldThrow()
    {
        var entry = CreateEntry();
        entry.Execute();
        Assert.ThrowsExactly<InvalidOperationException>(() => entry.Execute());
    }

    [TestMethod]
    public void Execute_WhenAlreadyCompleted_ShouldThrow()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Complete();
        Assert.ThrowsExactly<InvalidOperationException>(() => entry.Execute());
    }

    [TestMethod]
    public void Complete_ShouldSetIsCompletedAndClearIsExecuting()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Complete();
        Assert.IsTrue(entry.IsCompleted);
        Assert.IsFalse(entry.IsExecuting);
        Assert.IsNotNull(entry.CompletedAt);
    }

    [TestMethod]
    public void Complete_WhenAlreadyCompleted_ShouldThrow()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Complete();
        Assert.ThrowsExactly<InvalidOperationException>(() => entry.Complete());
    }

    [TestMethod]
    public void Fail_WithGenericException_ShouldSetErrorCode500()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Fail(new Exception("something broke"));
        Assert.IsTrue(entry.IsCompleted);
        Assert.IsFalse(entry.IsExecuting);
        Assert.AreEqual(500, entry.ErrorCode);
        Assert.AreEqual("something broke", entry.ErrorMessage);
    }

    [TestMethod]
    public void Fail_WithActivityException_ShouldUseActivityErrorState()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Fail(new BadRequestActivityException("bad input"));
        Assert.AreEqual(400, entry.ErrorCode);
    }

    [TestMethod]
    public void Cancel_ShouldSetIsCancelledAndReason()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Cancel("gateway sibling completed");
        Assert.IsTrue(entry.IsCancelled);
        Assert.IsTrue(entry.IsCompleted);
        Assert.AreEqual("gateway sibling completed", entry.CancellationReason);
    }

    [TestMethod]
    public void ErrorState_ShouldReturnNullWhenNoError()
    {
        var entry = CreateEntry();
        Assert.IsNull(entry.ErrorState);
    }

    [TestMethod]
    public void ErrorState_ShouldReturnValueAfterFail()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Fail(new Exception("err"));
        Assert.IsNotNull(entry.ErrorState);
        Assert.AreEqual(500, entry.ErrorState!.Code);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~ActivityInstanceEntryTests" -v n`
Expected: FAIL — properties/methods don't exist yet

**Step 3: Add new fields and methods to ActivityInstanceEntry**

```csharp
// ActivityInstanceEntry.cs — add these fields and methods alongside existing ones
using Fleans.Domain.Errors;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class ActivityInstanceEntry
{
    public ActivityInstanceEntry(Guid activityInstanceId, string activityId, Guid workflowInstanceId, Guid? scopeId = null)
    {
        ActivityInstanceId = activityInstanceId;
        ActivityId = activityId;
        WorkflowInstanceId = workflowInstanceId;
        ScopeId = scopeId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public ActivityInstanceEntry(Guid activityInstanceId, string activityId, Guid workflowInstanceId, Guid? scopeId, int multiInstanceIndex)
        : this(activityInstanceId, activityId, workflowInstanceId, scopeId)
    {
        MultiInstanceIndex = multiInstanceIndex;
    }

    private ActivityInstanceEntry() { }

    // Identity
    [Id(0)] public Guid ActivityInstanceId { get; private set; }
    [Id(1)] public string ActivityId { get; private set; } = null!;
    [Id(2)] public Guid WorkflowInstanceId { get; private set; }

    // Completion (existing)
    [Id(3)] public bool IsCompleted { get; private set; }

    // Scoping (existing)
    [Id(4)] public Guid? ChildWorkflowInstanceId { get; private set; }
    [Id(5)] public Guid? ScopeId { get; private set; }
    [Id(6)] public int? MultiInstanceIndex { get; private set; }

    // Execution state (new — from ActivityInstanceState)
    [Id(7)] public string? ActivityType { get; private set; }
    [Id(8)] public bool IsExecuting { get; private set; }
    [Id(9)] public bool IsCancelled { get; private set; }
    [Id(10)] public string? CancellationReason { get; private set; }
    [Id(11)] public Guid VariablesId { get; private set; }
    [Id(12)] public int? ErrorCode { get; private set; }
    [Id(13)] public string? ErrorMessage { get; private set; }
    [Id(14)] public Guid? TokenId { get; private set; }
    [Id(15)] public int? MultiInstanceTotal { get; private set; }

    // Timestamps (new — from ActivityInstanceState)
    [Id(16)] public DateTimeOffset? CreatedAt { get; private set; }
    [Id(17)] public DateTimeOffset? ExecutionStartedAt { get; private set; }
    [Id(18)] public DateTimeOffset? CompletedAt { get; private set; }

    // Computed
    public ActivityErrorState? ErrorState =>
        ErrorCode is not null ? new ActivityErrorState(ErrorCode.Value, ErrorMessage!) : null;

    // Existing methods
    public void SetChildWorkflowInstanceId(Guid childId) => ChildWorkflowInstanceId = childId;
    internal void MarkCompleted() => IsCompleted = true;

    // State transition methods (from ActivityInstanceState)
    public void Execute()
    {
        if (IsExecuting)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already executing.");
        if (IsCompleted)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already completed — cannot execute again.");
        IsExecuting = true;
        ExecutionStartedAt = DateTimeOffset.UtcNow;
    }

    public void Complete()
    {
        if (IsCompleted)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already completed.");
        IsExecuting = false;
        IsCompleted = true;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(Exception exception)
    {
        if (IsCompleted)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already completed — cannot fail.");
        if (exception is ActivityException activityException)
        {
            var errorState = activityException.GetActivityErrorState();
            ErrorCode = errorState.Code;
            ErrorMessage = errorState.Message;
        }
        else
        {
            ErrorCode = 500;
            ErrorMessage = exception.Message;
        }
        IsExecuting = false;
        IsCompleted = true;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Cancel(string reason)
    {
        if (IsCompleted)
            throw new InvalidOperationException($"Activity '{ActivityId}' is already completed — cannot cancel.");
        IsCancelled = true;
        CancellationReason = reason;
        IsExecuting = false;
        IsCompleted = true;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void ResetExecuting()
    {
        if (!IsExecuting) return;
        IsExecuting = false;
    }

    // Setters for initialization
    public void SetActivity(string activityId, string activityType)
    {
        ActivityType = activityType;
    }

    public void SetVariablesId(Guid id) => VariablesId = id;
    public void SetMultiInstanceIndex(int index) => MultiInstanceIndex = index;
    public void SetMultiInstanceTotal(int total) => MultiInstanceTotal = total;
    public void SetTokenId(Guid id) => TokenId = id;
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~ActivityInstanceEntryTests" -v n`
Expected: PASS

**Step 5: Run all existing tests to verify no regressions**

Run: `dotnet test src/Fleans/Fleans/ -v n`
Expected: All PASS (new fields don't break existing serialization — Orleans [Id] attributes ensure backward compatibility)

**Step 6: Commit**

```bash
git add src/Fleans/Fleans/Fleans.Domain/States/ActivityInstanceEntry.cs src/Fleans/Fleans/Fleans.Domain.Tests/ActivityInstanceEntryTests.cs
git commit -m "feat: enrich ActivityInstanceEntry with execution state fields and transition methods"
```

---

### Task 2: Create domain event types

**Files:**
- Create: `src/Fleans/Fleans/Fleans.Domain/Events/WorkflowDomainEvents.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowDomainEventsTests.cs`

**Step 1: Write tests verifying event construction**

```csharp
// WorkflowDomainEventsTests.cs
using System.Dynamic;
using Fleans.Domain.Events;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowDomainEventsTests
{
    [TestMethod]
    public void WorkflowStarted_ShouldHoldInstanceIdAndProcessDefinitionId()
    {
        var id = Guid.NewGuid();
        var evt = new WorkflowStarted(id, "process-1");
        Assert.AreEqual(id, evt.InstanceId);
        Assert.AreEqual("process-1", evt.ProcessDefinitionId);
    }

    [TestMethod]
    public void ActivitySpawned_ShouldHoldAllFields()
    {
        var evt = new ActivitySpawned(Guid.NewGuid(), "task1", "ScriptTask",
            Guid.NewGuid(), null, null, null);
        Assert.AreEqual("task1", evt.ActivityId);
        Assert.AreEqual("ScriptTask", evt.ActivityType);
    }

    [TestMethod]
    public void ActivityCompleted_ShouldHoldVariables()
    {
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["x"] = 10;
        var evt = new ActivityCompleted(Guid.NewGuid(), Guid.NewGuid(), vars);
        Assert.AreEqual(10, ((IDictionary<string, object?>)evt.Variables)["x"]);
    }

    [TestMethod]
    public void AllDomainEvents_ShouldImplementIDomainEvent()
    {
        var events = new IDomainEvent[]
        {
            new WorkflowStarted(Guid.NewGuid(), "p1"),
            new WorkflowCompleted(),
            new ActivitySpawned(Guid.NewGuid(), "a1", "T", Guid.NewGuid(), null, null, null),
            new ActivityExecutionStarted(Guid.NewGuid()),
            new ActivityCompleted(Guid.NewGuid(), Guid.NewGuid(), new ExpandoObject()),
            new ActivityFailed(Guid.NewGuid(), 500, "err"),
            new ActivityCancelled(Guid.NewGuid(), "reason"),
            new VariablesMerged(Guid.NewGuid(), new ExpandoObject()),
            new ChildVariableScopeCreated(Guid.NewGuid(), Guid.NewGuid()),
            new VariableScopeCloned(Guid.NewGuid(), Guid.NewGuid()),
            new VariableScopesRemoved([Guid.NewGuid()]),
            new ConditionSequencesAdded(Guid.NewGuid(), ["seq1"]),
            new ConditionSequenceEvaluated(Guid.NewGuid(), "seq1", true),
            new GatewayForkCreated(Guid.NewGuid(), null),
            new GatewayForkTokenAdded(Guid.NewGuid(), Guid.NewGuid()),
            new GatewayForkRemoved(Guid.NewGuid()),
            new ParentInfoSet(Guid.NewGuid(), "parentActivity"),
        };

        Assert.AreEqual(17, events.Length);
        foreach (var evt in events)
            Assert.IsInstanceOfType<IDomainEvent>(evt);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~WorkflowDomainEventsTests" -v n`
Expected: FAIL — types don't exist

**Step 3: Create all domain event records**

```csharp
// WorkflowDomainEvents.cs
using System.Dynamic;

namespace Fleans.Domain.Events;

// Workflow lifecycle
public record WorkflowStarted(Guid InstanceId, string? ProcessDefinitionId) : IDomainEvent;
public record WorkflowCompleted() : IDomainEvent;

// Activity lifecycle
public record ActivitySpawned(
    Guid ActivityInstanceId, string ActivityId, string ActivityType,
    Guid VariablesId, Guid? ScopeId, int? MultiInstanceIndex,
    Guid? TokenId) : IDomainEvent;
public record ActivityExecutionStarted(Guid ActivityInstanceId) : IDomainEvent;
public record ActivityCompleted(
    Guid ActivityInstanceId, Guid VariablesId, ExpandoObject Variables) : IDomainEvent;
public record ActivityFailed(
    Guid ActivityInstanceId, int ErrorCode, string ErrorMessage) : IDomainEvent;
public record ActivityCancelled(Guid ActivityInstanceId, string Reason) : IDomainEvent;

// Variable management
public record VariablesMerged(Guid VariablesId, ExpandoObject Variables) : IDomainEvent;
public record ChildVariableScopeCreated(Guid ScopeId, Guid ParentScopeId) : IDomainEvent;
public record VariableScopeCloned(Guid NewScopeId, Guid SourceScopeId) : IDomainEvent;
public record VariableScopesRemoved(List<Guid> ScopeIds) : IDomainEvent;

// Gateway/token management
public record ConditionSequencesAdded(
    Guid GatewayInstanceId, string[] SequenceFlowIds) : IDomainEvent;
public record ConditionSequenceEvaluated(
    Guid GatewayInstanceId, string SequenceFlowId, bool Result) : IDomainEvent;
public record GatewayForkCreated(Guid ForkInstanceId, Guid? ConsumedTokenId) : IDomainEvent;
public record GatewayForkTokenAdded(Guid ForkInstanceId, Guid TokenId) : IDomainEvent;
public record GatewayForkRemoved(Guid ForkInstanceId) : IDomainEvent;

// Parent/child
public record ParentInfoSet(Guid ParentInstanceId, string ParentActivityId) : IDomainEvent;
```

**Step 4: Run tests**

Run: `dotnet test src/Fleans/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~WorkflowDomainEventsTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Fleans/Fleans/Fleans.Domain/Events/WorkflowDomainEvents.cs src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowDomainEventsTests.cs
git commit -m "feat: add domain event types for workflow execution aggregate"
```

---

### Task 3: Create infrastructure effects model

**Files:**
- Create: `src/Fleans/Fleans/Fleans.Domain/Effects/InfrastructureEffects.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/InfrastructureEffectsTests.cs`

**Step 1: Write tests**

```csharp
// InfrastructureEffectsTests.cs
using Fleans.Domain.Effects;

namespace Fleans.Domain.Tests;

[TestClass]
public class InfrastructureEffectsTests
{
    [TestMethod]
    public void AllEffects_ShouldImplementIInfrastructureEffect()
    {
        var effects = new IInfrastructureEffect[]
        {
            new RegisterTimerEffect(Guid.NewGuid(), Guid.NewGuid(), "timer1", TimeSpan.FromSeconds(5)),
            new UnregisterTimerEffect(Guid.NewGuid(), Guid.NewGuid(), "timer1"),
            new SubscribeMessageEffect("msg", "key", Guid.NewGuid(), Guid.NewGuid()),
            new UnsubscribeMessageEffect("msg", "key"),
            new SubscribeSignalEffect("sig", Guid.NewGuid(), Guid.NewGuid()),
            new UnsubscribeSignalEffect("sig"),
            new ThrowSignalEffect("sig"),
            new StartChildWorkflowEffect(Guid.NewGuid(), "process-key", new System.Dynamic.ExpandoObject(), "callAct"),
            new NotifyParentCompletedEffect(Guid.NewGuid(), "parentAct", new System.Dynamic.ExpandoObject()),
            new NotifyParentFailedEffect(Guid.NewGuid(), "parentAct", new Exception("err")),
            new PublishDomainEventEffect(new Events.WorkflowCompleted()),
            new CancelActivitySubscriptionsEffect("act1", Guid.NewGuid()),
        };

        Assert.AreEqual(12, effects.Length);
    }
}
```

**Step 2: Run tests — FAIL**

Run: `dotnet test src/Fleans/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~InfrastructureEffectsTests" -v n`

**Step 3: Create effect records**

```csharp
// InfrastructureEffects.cs
using System.Dynamic;
using Fleans.Domain.Events;

namespace Fleans.Domain.Effects;

public interface IInfrastructureEffect { }

// Timer
public record RegisterTimerEffect(
    Guid WorkflowInstanceId, Guid HostActivityInstanceId,
    string TimerActivityId, TimeSpan DueTime) : IInfrastructureEffect;
public record UnregisterTimerEffect(
    Guid WorkflowInstanceId, Guid HostActivityInstanceId,
    string TimerActivityId) : IInfrastructureEffect;

// Message
public record SubscribeMessageEffect(
    string MessageName, string CorrelationKey,
    Guid WorkflowInstanceId, Guid HostActivityInstanceId) : IInfrastructureEffect;
public record UnsubscribeMessageEffect(
    string MessageName, string CorrelationKey) : IInfrastructureEffect;

// Signal
public record SubscribeSignalEffect(
    string SignalName, Guid WorkflowInstanceId,
    Guid HostActivityInstanceId) : IInfrastructureEffect;
public record UnsubscribeSignalEffect(string SignalName) : IInfrastructureEffect;
public record ThrowSignalEffect(string SignalName) : IInfrastructureEffect;

// Child workflows
public record StartChildWorkflowEffect(
    Guid ChildInstanceId, string ProcessDefinitionKey,
    ExpandoObject InputVariables, string ParentActivityId) : IInfrastructureEffect;

// Parent notifications
public record NotifyParentCompletedEffect(
    Guid ParentInstanceId, string ParentActivityId,
    ExpandoObject Variables) : IInfrastructureEffect;
public record NotifyParentFailedEffect(
    Guid ParentInstanceId, string ParentActivityId,
    Exception Exception) : IInfrastructureEffect;

// Event publishing
public record PublishDomainEventEffect(IDomainEvent Event) : IInfrastructureEffect;

// Activity cleanup
public record CancelActivitySubscriptionsEffect(
    string ActivityId, Guid ActivityInstanceId) : IInfrastructureEffect;
```

**Step 4: Run tests — PASS**

Run: `dotnet test src/Fleans/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~InfrastructureEffectsTests" -v n`

**Step 5: Run all tests — PASS**

Run: `dotnet test src/Fleans/Fleans/ -v n`

**Step 6: Commit**

```bash
git add src/Fleans/Fleans/Fleans.Domain/Effects/InfrastructureEffects.cs src/Fleans/Fleans/Fleans.Domain.Tests/InfrastructureEffectsTests.cs
git commit -m "feat: add infrastructure effect types for grain coordination"
```

---

## Phase 2: WorkflowExecution Aggregate (Core)

### Task 4: Aggregate skeleton with Emit/Apply and Start

**Files:**
- Create: `src/Fleans/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowExecutionTests.cs`

This task creates the aggregate with `Emit`/`Apply`, `Start()`, `GetPendingActivities()`, `MarkExecuting()`, and `MarkCompleted()`.

**Step 1: Write failing tests**

```csharp
// WorkflowExecutionTests.cs
using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionTests
{
    private static (WorkflowExecution execution, WorkflowInstanceState state) CreateExecution(
        List<Activity> activities, List<SequenceFlow> flows,
        string workflowId = "wf1", string processDefinitionId = "pd1")
    {
        var definition = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = activities,
            SequenceFlows = flows,
            ProcessDefinitionId = processDefinitionId
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        return (execution, state);
    }

    [TestMethod]
    public void Start_ShouldEmitWorkflowStartedAndActivitySpawned()
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");
        var (execution, state) = CreateExecution(
            [start, end],
            [new SequenceFlow("seq1", start, end)]);

        execution.Start();

        var events = execution.GetUncommittedEvents();
        Assert.IsTrue(events.Count >= 2);
        Assert.IsInstanceOfType<WorkflowStarted>(events[0]);
        Assert.IsInstanceOfType<ActivitySpawned>(events[1]);
        Assert.AreEqual("start1", ((ActivitySpawned)events[1]).ActivityId);
    }

    [TestMethod]
    public void Start_ShouldSetStateStarted()
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);

        execution.Start();

        Assert.IsTrue(state.IsStarted);
        Assert.AreEqual(1, state.Entries.Count);
    }

    [TestMethod]
    public void GetPendingActivities_ShouldReturnNotExecutingNotCompleted()
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);

        execution.Start();

        var pending = execution.GetPendingActivities();
        Assert.AreEqual(1, pending.Count);
        Assert.AreEqual("start1", pending[0].ActivityId);
    }

    [TestMethod]
    public void MarkExecuting_ShouldEmitActivityExecutionStarted()
    {
        var start = new StartEvent("start1");
        var (execution, state) = CreateExecution([start], []);
        execution.Start();
        var pending = execution.GetPendingActivities();

        execution.MarkExecuting(pending[0].ActivityInstanceId);

        var events = execution.GetUncommittedEvents();
        Assert.IsTrue(events.Any(e => e is ActivityExecutionStarted));
        Assert.AreEqual(0, execution.GetPendingActivities().Count);
    }

    [TestMethod]
    public void ClearUncommittedEvents_ShouldClearList()
    {
        var start = new StartEvent("start1");
        var (execution, _) = CreateExecution([start], []);
        execution.Start();

        Assert.IsTrue(execution.GetUncommittedEvents().Count > 0);
        execution.ClearUncommittedEvents();
        Assert.AreEqual(0, execution.GetUncommittedEvents().Count);
    }
}
```

**Step 2: Run tests — FAIL**

Run: `dotnet test src/Fleans/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~WorkflowExecutionTests" -v n`

**Step 3: Implement aggregate skeleton**

```csharp
// WorkflowExecution.cs
using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.States;

namespace Fleans.Domain.Aggregates;

public record PendingActivity(Guid ActivityInstanceId, string ActivityId);

public class WorkflowExecution
{
    private readonly WorkflowInstanceState _state;
    private readonly IWorkflowDefinition _definition;
    private readonly List<IDomainEvent> _uncommittedEvents = new();

    public WorkflowExecution(WorkflowInstanceState state, IWorkflowDefinition definition)
    {
        _state = state;
        _definition = definition;
    }

    public WorkflowInstanceState State => _state;

    // --- Core ES mechanism ---

    private void Emit(IDomainEvent @event)
    {
        Apply(@event);
        _uncommittedEvents.Add(@event);
    }

    private void Apply(IDomainEvent @event)
    {
        switch (@event)
        {
            case WorkflowStarted e:
                _state.StartWith(e.InstanceId, e.ProcessDefinitionId,
                    /* entry added by ActivitySpawned */ null!, Guid.NewGuid());
                _state.Start();
                break;
            case ActivitySpawned e:
                var entry = new ActivityInstanceEntry(
                    e.ActivityInstanceId, e.ActivityId, _state.Id, e.ScopeId);
                entry.SetActivity(e.ActivityId, e.ActivityType);
                entry.SetVariablesId(e.VariablesId);
                if (e.MultiInstanceIndex.HasValue)
                    entry.SetMultiInstanceIndex(e.MultiInstanceIndex.Value);
                if (e.TokenId.HasValue)
                    entry.SetTokenId(e.TokenId.Value);
                _state.AddEntries([entry]);
                break;
            case ActivityExecutionStarted e:
                _state.GetActiveEntry(e.ActivityInstanceId).Execute();
                break;
            case ActivityCompleted e:
                _state.GetActiveEntry(e.ActivityInstanceId).Complete();
                _state.MergeState(e.VariablesId, e.Variables);
                break;
            case ActivityFailed e:
                var failEntry = _state.GetActiveEntry(e.ActivityInstanceId);
                failEntry.Fail(new InternalActivityException(e.ErrorCode, e.ErrorMessage));
                break;
            case ActivityCancelled e:
                _state.GetActiveEntry(e.ActivityInstanceId).Cancel(e.Reason);
                break;
            case VariablesMerged e:
                _state.MergeState(e.VariablesId, e.Variables);
                break;
            case ChildVariableScopeCreated e:
                _state.AddChildVariableState(e.ParentScopeId);
                break;
            case VariableScopeCloned e:
                _state.AddCloneOfVariableState(e.SourceScopeId);
                break;
            case VariableScopesRemoved e:
                _state.RemoveVariableStates(e.ScopeIds);
                break;
            case ConditionSequencesAdded e:
                _state.AddConditionSequenceStates(e.GatewayInstanceId, e.SequenceFlowIds);
                break;
            case ConditionSequenceEvaluated e:
                _state.SetConditionSequenceResult(e.GatewayInstanceId, e.SequenceFlowId, e.Result);
                break;
            case GatewayForkCreated e:
                _state.CreateGatewayFork(e.ForkInstanceId, e.ConsumedTokenId);
                break;
            case GatewayForkTokenAdded e:
                _state.FindForkByToken(e.ForkInstanceId)
                    ?? throw new InvalidOperationException("Fork not found");
                _state.GatewayForks.First(f => f.ForkInstanceId == e.ForkInstanceId)
                    .CreatedTokenIds.Add(e.TokenId);
                break;
            case GatewayForkRemoved e:
                _state.RemoveGatewayFork(e.ForkInstanceId);
                break;
            case ParentInfoSet e:
                _state.SetParentInfo(e.ParentInstanceId, e.ParentActivityId);
                break;
            case WorkflowCompleted:
                _state.Complete();
                break;
        }
    }

    public IReadOnlyList<IDomainEvent> GetUncommittedEvents() => _uncommittedEvents.AsReadOnly();
    public void ClearUncommittedEvents() => _uncommittedEvents.Clear();

    // --- Public API ---

    public IReadOnlyList<IInfrastructureEffect> Start()
    {
        var instanceId = _state.Id != Guid.Empty ? _state.Id : Guid.NewGuid();
        Emit(new WorkflowStarted(instanceId, _definition.ProcessDefinitionId));

        var startActivity = _definition.Activities
            .FirstOrDefault(a => a is StartEvent or MessageStartEvent or SignalStartEvent or TimerStartEvent)
            ?? throw new InvalidOperationException("No start event found");

        var variablesId = _state.VariableStates.First().Id;
        Emit(new ActivitySpawned(
            Guid.NewGuid(), startActivity.ActivityId, startActivity.GetType().Name,
            variablesId, null, null, null));

        return [];
    }

    public IReadOnlyList<PendingActivity> GetPendingActivities()
    {
        return _state.GetActiveActivities()
            .Where(e => !e.IsExecuting)
            .Select(e => new PendingActivity(e.ActivityInstanceId, e.ActivityId))
            .ToList();
    }

    public void MarkExecuting(Guid activityInstanceId)
    {
        Emit(new ActivityExecutionStarted(activityInstanceId));
    }

    public void MarkCompleted(Guid activityInstanceId)
    {
        var entry = _state.GetActiveEntry(activityInstanceId);
        Emit(new ActivityCompleted(activityInstanceId, entry.VariablesId, new ExpandoObject()));
    }
}

// Helper exception for Apply(ActivityFailed)
internal class InternalActivityException : Exception
{
    public int Code { get; }
    public InternalActivityException(int code, string message) : base(message) => Code = code;
}
```

> **Note:** The `Apply(WorkflowStarted)` handler calls `StartWith` which needs an entry — this coupling with `ActivitySpawned` may require adjusting `WorkflowInstanceState.StartWith` to not require an entry parameter, or splitting the initialization. The implementing engineer should adapt `StartWith` to work with the ES flow (initialize without entry, then add entry via `ActivitySpawned`).

**Step 4: Run tests — PASS**

Run: `dotnet test src/Fleans/Fleans/Fleans.Domain.Tests/ --filter "FullyQualifiedName~WorkflowExecutionTests" -v n`

**Step 5: Run all tests — PASS**

Run: `dotnet test src/Fleans/Fleans/ -v n`

**Step 6: Commit**

```bash
git add src/Fleans/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowExecutionTests.cs
git commit -m "feat: add WorkflowExecution aggregate skeleton with Emit/Apply and Start"
```

---

### Task 5: ProcessCommands — translate IExecutionCommand to effects

**Files:**
- Modify: `src/Fleans/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowExecutionProcessCommandsTests.cs`

Add `ProcessCommands()` method that handles each `IExecutionCommand` type and returns infrastructure effects. Each command type produces either domain events (state changes), infrastructure effects (external actions), or both.

This is a large method — test each command type individually:

- `CompleteWorkflowCommand` → emits `WorkflowCompleted`
- `SpawnActivityCommand` → emits `ActivitySpawned`
- `OpenSubProcessCommand` → emits `ChildVariableScopeCreated` + `ActivitySpawned`
- `RegisterTimerCommand` → returns `RegisterTimerEffect`
- `RegisterMessageCommand` → returns `SubscribeMessageEffect`
- `RegisterSignalCommand` → returns `SubscribeSignalEffect`
- `StartChildWorkflowCommand` → returns `StartChildWorkflowEffect`
- `AddConditionsCommand` → emits `ConditionSequencesAdded` + returns `PublishDomainEventEffect`
- `ThrowSignalCommand` → returns `ThrowSignalEffect`

Follow TDD: write one test per command type, implement the handler, verify pass, repeat. Commit after all command types are handled.

---

### Task 6: ResolveTransitions — transition logic with token propagation

**Files:**
- Modify: `src/Fleans/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowExecutionTransitionTests.cs`

Port `TransitionToNextActivity()` from `WorkflowInstance.Execution.cs:254-309` and `PropagateToken()` from lines 200-252. The method:

1. Finds completed entries (via local state — no grain calls needed)
2. For each completed, non-failed entry: calls `activity.GetNextActivities()` (still async — the grain mediates this)
3. Emits `ActivitySpawned` for each next activity
4. Handles token propagation: `GatewayForkCreated`, `GatewayForkTokenAdded`, `GatewayForkRemoved` events
5. Handles join gateway deduplication
6. Calls scope completion detection

> **Important note for implementer:** `GetNextActivities()` on Activity is async and takes `IWorkflowExecutionContext`/`IActivityExecutionContext`. Since the aggregate is synchronous, `ResolveTransitions` must be adapted. Two options:
> - Make `ResolveTransitions` take pre-fetched transition data as a parameter (grain calls `GetNextActivities` then passes results to aggregate)
> - Keep `ResolveTransitions` in the grain, calling aggregate helpers for each sub-decision
>
> Recommend option 1: the grain calls `activity.GetNextActivities(workflowContext, activityContext, definition)` for each completed activity, then passes the list of `(ActivityInstanceEntry, List<ActivityTransition>)` tuples to `aggregate.ResolveTransitions(transitions)`.

Test with:
- Simple sequential flow (StartEvent → Task → EndEvent)
- Parallel gateway fork (one activity → two branches)
- Parallel gateway join (two branches converge)

---

### Task 7: CompleteActivity / FailActivity

**Files:**
- Modify: `src/Fleans/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowExecutionActivityLifecycleTests.cs`

Port from `WorkflowInstance.ActivityLifecycle.cs`:

**CompleteActivity (lines 71-99):**
- Emits `ActivityCompleted` (variable merge happens in Apply)
- Returns `UnregisterTimerEffect`, `UnsubscribeMessageEffect`, `UnsubscribeSignalEffect` for boundary cleanup
- Calls internal `CancelEventBasedGatewaySiblings` (emits `ActivityCancelled` + returns unsubscribe effects)

**FailActivity (lines 314-397 — `FailActivityWithBoundaryCheck`):**
- Emits `ActivityFailed`
- Searches for boundary error handler in definition
- If found: emits `ActivityCancelled` for attached activity + `ActivitySpawned` for boundary event
- If not found: handles multi-instance host failure, or just marks entry completed
- Returns appropriate effects

Test with:
- Complete activity → variables merged, entry completed
- Fail with generic exception → error code 500
- Fail with `BadRequestActivityException` → error code 400
- Fail with boundary error handler → boundary event spawned
- Stale callback (already completed) → ignored

---

### Task 8: Event handling — timer, message, signal delivery

**Files:**
- Modify: `src/Fleans/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowExecutionEventHandlingTests.cs`

Port from `WorkflowInstance.EventHandling.cs`:

**HandleTimerFired (lines 10-46):**
- Check if entry is still active (stale guard)
- If boundary timer: delegate to boundary handling logic
- If intermediate catch: emit `ActivityCompleted`

**HandleMessageDelivery (lines 51-83):**
- Check if entry is still active (stale guard)
- If boundary message: delegate to boundary handling
- If intermediate catch: emit `ActivityCompleted` with delivered variables

**HandleSignalDelivery (lines 99-122):**
- Same pattern as message

Test with:
- Timer fires → catch event completed
- Timer fires on already-completed activity → ignored (stale)
- Message delivered → variables merged, catch event completed
- Signal delivered → catch event completed

---

### Task 9: Scope completion — subprocess and multi-instance

**Files:**
- Modify: `src/Fleans/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowExecutionScopeCompletionTests.cs`

Port from `WorkflowInstance.Execution.cs`:

**CompleteFinishedSubProcessScopes (lines 311-368):**
- Detects when all entries in a subprocess scope are completed
- Emits `ActivityCompleted` for the subprocess host
- Handles output variable aggregation for multi-instance

**TryCompleteMultiInstanceHost (lines 370-471):**
- Detects when all iterations are done (parallel) or spawns next iteration (sequential)
- Emits `ActivitySpawned` for sequential next iteration
- Emits `ActivityCompleted` for host when all done
- Handles output collection aggregation

Test with:
- SubProcess with all children completed → host completed
- MultiInstance parallel with all iterations done → host completed
- MultiInstance sequential iteration complete → next iteration spawned

---

### Task 10: Boundary event handling

**Files:**
- Modify: `src/Fleans/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowExecutionBoundaryTests.cs`

Port from `BoundaryEventHandler.cs` (lines 20-238):

**Interrupting boundaries:**
- Emit `ActivityCancelled` for the attached activity
- Emit `ActivityCancelled` for all scope children
- Emit `ActivitySpawned` for the boundary event activity itself
- Return unsubscribe effects for other boundary subscriptions

**Non-interrupting boundaries:**
- Leave attached activity running
- Emit `VariableScopeCloned` for cloned variables
- Emit `ActivitySpawned` for boundary activity with cloned scope

**Error boundaries:**
- Emit `ActivityCancelled` for scope children
- Emit `ActivitySpawned` for boundary error event

Test with:
- Interrupting timer boundary → attached activity cancelled, boundary spawned
- Non-interrupting timer boundary → attached activity stays, boundary spawned with cloned vars
- Error boundary → scope children cancelled, boundary spawned

---

### Task 11: Condition sequence handling and child workflows

**Files:**
- Modify: `src/Fleans/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/WorkflowExecutionConditionAndChildTests.cs`

**CompleteConditionSequence** (from ActivityLifecycle.cs lines 261-297):
- Emits `ConditionSequenceEvaluated`
- Checks if all conditions for the gateway are evaluated
- If so: marks gateway as completed (domain logic determines whether to take default flow)

**OnChildWorkflowCompleted** (lines 233-248):
- Maps output variables
- Emits `ActivityCompleted` for the call activity

**OnChildWorkflowFailed** (lines 250-259):
- Calls FailActivity logic for the call activity

Test with:
- Condition evaluates true → gateway completed
- All conditions false + default flow → gateway completed via default
- Child workflow completed → call activity completed with mapped variables
- Child workflow failed → call activity failed with boundary check

---

## Phase 3: Grain Integration

### Task 12: Create ActivityExecutionContextAdapter

**Files:**
- Create: `src/Fleans/Fleans/Fleans.Application/Adapters/ActivityExecutionContextAdapter.cs`
- Test: `src/Fleans/Fleans/Fleans.Domain.Tests/ActivityExecutionContextAdapterTests.cs`

Implements `IActivityExecutionContext` by delegating to `WorkflowExecution` aggregate. All methods are synchronous (return `ValueTask.FromResult`). The adapter also collects domain events published by activities (via `PublishEvent`) into a list that the grain reads after `ExecuteAsync` returns.

---

### Task 13: Refactor WorkflowInstance grain to use aggregate

**Files:**
- Modify: `src/Fleans/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`
- Modify: `src/Fleans/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs`
- Modify: `src/Fleans/Fleans/Fleans.Application/Grains/WorkflowInstance.ActivityLifecycle.cs`
- Modify: `src/Fleans/Fleans/Fleans.Application/Grains/WorkflowInstance.EventHandling.cs`
- Modify: `src/Fleans/Fleans/Fleans.Application/Grains/WorkflowInstance.StateFacade.cs`

Replace all BPMN logic with aggregate delegation. Each grain entry point becomes:
1. Call `_execution.Method()` → get effects
2. Call `PerformEffects(effects)`
3. Call `RunExecutionLoop()`
4. Call `LogAndClearEvents()`
5. Call `_state.WriteStateAsync()`

Add `PerformEffects()` method with switch over all `IInfrastructureEffect` types.

Delegate `IWorkflowExecutionContext` methods to the aggregate's state.

**Testing:** Run full integration test suite after this task:

Run: `dotnet test src/Fleans/Fleans/ -v n`
Expected: All PASS — external behavior unchanged

---

## Phase 4: Cleanup

### Task 14: Delete ActivityInstance grain and related code

**Files:**
- Delete: `src/Fleans/Fleans/Fleans.Application/Grains/ActivityInstance.cs`
- Delete: `src/Fleans/Fleans/Fleans.Application/Grains/IActivityInstanceGrain.cs`
- Delete: `src/Fleans/Fleans/Fleans.Domain/States/ActivityInstanceState.cs`
- Delete: `src/Fleans/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs`
- Delete: `src/Fleans/Fleans/Fleans.Application/Services/IBoundaryEventHandler.cs`
- Modify: `src/Fleans/Fleans/Fleans.Domain/GrainStorageNames.cs` — remove `ActivityInstances` constant
- Delete: `src/Fleans/Fleans/Fleans.Persistence/EfCoreActivityInstanceGrainStorage.cs`
- Modify: `src/Fleans/Fleans/Fleans.Persistence/DependencyInjection.cs` — remove ActivityInstance registration

Run: `dotnet build src/Fleans/Fleans/`
Fix any compilation errors from removed references.

Run: `dotnet test src/Fleans/Fleans/ -v n`
Expected: All PASS

Commit: `git commit -m "refactor: delete ActivityInstance grain and BoundaryEventHandler — logic moved to aggregate"`

---

### Task 15: Update EF Core persistence

**Files:**
- Modify: `src/Fleans/Fleans/Fleans.Persistence/FleanModelConfiguration.cs` — update ActivityInstanceEntries mapping with new fields
- Modify: `src/Fleans/Fleans/Fleans.Persistence/EfCoreWorkflowInstanceGrainStorage.cs` — update diff logic for enriched entries
- Modify: `src/Fleans/Fleans/Fleans.Persistence/FleanCommandDbContext.cs` — remove `ActivityInstances` DbSet
- Modify: `src/Fleans/Fleans/Fleans.Persistence/FleanQueryDbContext.cs` — remove `ActivityInstances` DbSet
- Modify: `src/Fleans/Fleans/Fleans.Persistence/WorkflowQueryService.cs` — update `GetStateSnapshot` and `ToActivitySnapshot` to read from enriched entries instead of joining ActivityInstances

Run: `dotnet build src/Fleans/Fleans/`
Run: `dotnet test src/Fleans/Fleans/ -v n`

Commit: `git commit -m "refactor: update EF Core persistence for enriched ActivityInstanceEntry"`

---

### Task 16: Consolidate grain partial files

**Files:**
- The grain should now be small enough to consolidate. Merge the remaining thin coordinator logic into at most 2 files:
  - `WorkflowInstance.cs` — fields, constructor, public entry points (all follow 5-step pattern)
  - `WorkflowInstance.Infrastructure.cs` — `PerformEffects`, `RunExecutionLoop`, `LogAndClearEvents`
- Delete emptied partial files: `WorkflowInstance.Execution.cs`, `WorkflowInstance.ActivityLifecycle.cs`, `WorkflowInstance.EventHandling.cs`, `WorkflowInstance.StateFacade.cs`

Run: `dotnet test src/Fleans/Fleans/ -v n`
Expected: All PASS

Commit: `git commit -m "refactor: consolidate WorkflowInstance grain into thin coordinator"`

---

### Task 17: Final verification

**Step 1:** Run full test suite

Run: `dotnet test src/Fleans/Fleans/ -v n`
Expected: All PASS

**Step 2:** Run Aspire stack locally

Run: `dotnet run --project src/Fleans/Fleans/Fleans.Aspire/`

Verify:
- Web UI loads at https://localhost:7141
- Deploy a workflow via API
- Start a workflow instance
- Verify execution completes

**Step 3:** Final commit if any fixes needed

---

## Dependency Graph

```
Task 1 (Enrich Entry) ─┐
Task 2 (Domain Events) ─┼─ Task 4 (Aggregate Skeleton)
Task 3 (Effects)       ─┘       │
                                ├─ Task 5 (ProcessCommands)
                                ├─ Task 6 (ResolveTransitions)
                                ├─ Task 7 (CompleteActivity/FailActivity)
                                ├─ Task 8 (Event Handling)
                                ├─ Task 9 (Scope Completion)
                                ├─ Task 10 (Boundary Events)
                                └─ Task 11 (Conditions/Child Workflows)
                                        │
                                Task 12 (Adapter) ─── Task 13 (Grain Refactor)
                                                            │
                                                    Task 14 (Delete Old Code)
                                                            │
                                                    Task 15 (EF Core Update)
                                                            │
                                                    Task 16 (Consolidate Grain)
                                                            │
                                                    Task 17 (Final Verification)
```

Tasks 5-11 can be done in any order (all depend only on Task 4). Tasks 1-3 are independent and can be parallelized.
