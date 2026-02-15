# Interface Segregation: Activity Context vs Grain Interfaces

**Date:** 2026-02-15
**Status:** Proposed

## Problem

`IWorkflowInstance` and `IActivityInstance` are Orleans grain interfaces (`IGrainWithGuidKey`) that live in `Fleans.Domain`. Every activity, the engine, event handlers, and external callers all share the same interfaces. This causes three problems:

1. **Domain depends on Orleans.** Activity code references `IGrainWithGuidKey`, `[ReadOnly]`, and grain-specific `ValueTask` semantics.
2. **Activities see methods they shouldn't call.** An activity can call `SetWorkflow()`, `StartWorkflow()`, `FailActivity()` on the workflow, or `SetActivity()`, `Fail()`, `SetVariablesId()` on the activity instance.
3. **Grain implementations live in Domain.** `WorkflowInstance.cs` and `ActivityInstance.cs` are grain classes in the Domain layer, violating Clean Architecture.

## Solution

Split each interface into two layers:

- **Domain context interfaces** — what activities use. No Orleans dependency.
- **Grain interfaces** — what the engine and orchestration use. Live in Application, extend the domain interfaces.

## Domain Context Interfaces

These live in `Fleans.Domain/` and have zero Orleans dependency.

### IActivityExecutionContext

```csharp
namespace Fleans.Domain;

public interface IActivityExecutionContext
{
    Task<Guid> GetActivityInstanceId();
    Task<string> GetActivityId();
    Task<bool> IsCompleted();
    Task Complete();
    Task Execute();
    Task PublishEvent(IDomainEvent domainEvent);
}
```

### IWorkflowExecutionContext

```csharp
namespace Fleans.Domain;

public interface IWorkflowExecutionContext
{
    Task<Guid> GetWorkflowInstanceId();
    Task<IWorkflowDefinition> GetWorkflowDefinition();
    Task Complete();

    // Condition/gateway state
    Task<IReadOnlyDictionary<Guid, ConditionSequenceState[]>> GetConditionSequenceStates();
    Task AddConditionSequenceStates(Guid activityInstanceId, string[] sequenceFlowIds);
    Task SetConditionSequenceResult(Guid activityInstanceId, string sequenceId, bool result);

    // State queries (ParallelGateway join logic)
    Task<IReadOnlyList<IActivityExecutionContext>> GetActiveActivities();
    Task<IReadOnlyList<IActivityExecutionContext>> GetCompletedActivities();
}
```

## Grain Interfaces (Application Layer)

These live in `Fleans.Application/`, extend `IGrainWithGuidKey`, and inherit from the domain context interfaces. The grain implementations satisfy both contracts.

### IActivityInstanceGrain

```csharp
namespace Fleans.Application;

public interface IActivityInstanceGrain : IGrainWithGuidKey, IActivityExecutionContext
{
    [ReadOnly]
    ValueTask<bool> IsExecuting();

    ValueTask<Guid> GetVariablesStateId();
    ValueTask SetActivity(string activityId, string activityType);
    ValueTask SetVariablesId(Guid guid);
    ValueTask Fail(Exception exception);
}
```

### IWorkflowInstanceGrain

```csharp
namespace Fleans.Application;

public interface IWorkflowInstanceGrain : IGrainWithGuidKey, IWorkflowExecutionContext
{
    ValueTask SetWorkflow(IWorkflowDefinition workflow);
    Task StartWorkflow();
    Task CompleteActivity(string activityId, ExpandoObject variables);
    Task FailActivity(string activityId, Exception exception);
    ValueTask<ExpandoObject> GetVariables(Guid variablesStateId);
}
```

## Activity Signature Change

All activities change parameter types from grain interfaces to domain context interfaces:

```csharp
// Before
internal override async Task ExecuteAsync(
    IWorkflowInstance workflowInstance,
    IActivityInstance activityInstance)

// After
internal override async Task ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext)
```

Method call bodies stay identical — `GetWorkflowDefinition()`, `Complete()`, `PublishEvent()`, `GetActivityInstanceId()`, etc. are all on the context interfaces.

## Dependency Graph

```
Domain (Orleans.Sdk — needed for [GenerateSerializer] source generators)
  - IActivityExecutionContext
  - IWorkflowExecutionContext
  - IWorkflowDefinition
  - Activities (use only domain context interfaces)
  - WorkflowInstanceState, ConditionSequenceState
  - Domain events, sequences
  ↑
Application (Orleans.Sdk)
  - IActivityInstanceGrain : IGrainWithGuidKey, IActivityExecutionContext
  - IWorkflowInstanceGrain : IGrainWithGuidKey, IWorkflowExecutionContext
  - ActivityInstance (grain implementation, moved from Domain)
  - WorkflowInstance (grain implementation, moved from Domain)
  - Event handlers, command/query services
  ↑
Infrastructure / Api / Web
```

## What Moves

| File | From | To | Notes |
|------|------|----|-------|
| `IActivityInstance.cs` | Domain | Split | Context interface stays in Domain as `IActivityExecutionContext.cs`; grain interface goes to Application as `IActivityInstanceGrain.cs` |
| `IWorkflowInstance.cs` | Domain | Split | Context interface stays in Domain as `IWorkflowExecutionContext.cs`; grain interface goes to Application as `IWorkflowInstanceGrain.cs` |
| `ActivityInstance.cs` | Domain | Application | Grain implementation |
| `WorkflowInstance.cs` | Domain | Application | Grain implementation |

## What Stays in Domain

- All activity classes (`Activity.cs`, `ScriptTask.cs`, `ExclusiveGateway.cs`, `ParallelGateway.cs`, etc.)
- `IWorkflowDefinition`, `WorkflowDefinition`
- Sequences (`SequenceFlow`, `ConditionalSequenceFlow`, `DefaultSequenceFlow`)
- Domain events (`IDomainEvent`, all event records)
- `WorkflowInstanceState`, `ConditionSequenceState`, and related state types

## Serialization

Domain keeps `[GenerateSerializer]` and `[Id]` attributes on activity records by referencing `Orleans.Sdk` — the full package is needed because `[GenerateSerializer]` uses source generators that require more than just attribute definitions.

## Caller Migration

| Caller | Before | After |
|--------|--------|-------|
| Activity classes | `IWorkflowInstance`, `IActivityInstance` | `IWorkflowExecutionContext`, `IActivityExecutionContext` |
| WorkflowInstance (engine) | `IActivityInstance` | `IActivityInstanceGrain` (engine-only methods), passes as `IActivityExecutionContext` to activities |
| Event handlers | `IWorkflowInstance`, `IActivityInstance` | `IWorkflowInstanceGrain`, `IActivityInstanceGrain` |
| CommandService / QueryService | `IWorkflowInstance` | `IWorkflowInstanceGrain` |
| Tests | `IWorkflowInstance`, `IActivityInstance` | `IWorkflowInstanceGrain`, `IActivityInstanceGrain` (tests exercise grain behavior) |

## Test Impact

Tests currently live in `Fleans.Domain.Tests` and create grains via `TestCluster`. Since grain implementations move to Application, tests should reference `Fleans.Application` and use the grain interfaces. No test logic changes — only type renames at call sites.

## Risks

- **Method signature mismatch:** Domain context uses `Task`, grains use `ValueTask`. Since grain interfaces inherit the domain interfaces and C# allows implicit `Task` in interface hierarchies, the grain implementation can satisfy both. Methods that exist on both layers keep `Task` return types.
- **InternalsVisibleTo:** Activities use `internal` methods. The `InternalsVisibleTo` currently points at `Fleans.Domain.Tests`. Since `WorkflowInstance` moves to Application and calls `Activity.ExecuteAsync()` (internal), Application needs `InternalsVisibleTo` as well.
