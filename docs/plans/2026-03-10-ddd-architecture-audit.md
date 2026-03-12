# DDD & Architecture Audit — Tech Debt Catalogue

**Date:** 2026-03-10
**Type:** Tech Debt Audit (domain purity focus)
**Status:** Approved

---

## Summary

This document catalogues DDD and architecture anti-patterns in the Fleans codebase. The core finding: the architecture was built outward from Orleans grain requirements rather than inward from BPMN domain semantics. The `WorkflowInstance` grain is the de facto aggregate root, but no real aggregate root exists in the domain layer.

---

## P0 — Critical Issues

### 1. Core BPMN Logic Lives in the Grain Layer

The `WorkflowInstance` grain (6 partial files, 1000+ lines) contains fundamental BPMN execution semantics that belong in the domain:

| Grain method | Domain concept |
|---|---|
| `ExecuteWorkflow()` | BPMN token execution loop |
| `TransitionToNextActivity()` | Sequence flow routing |
| `CompleteFinishedSubProcessScopes()` | Scope completion rules |
| `CancelEventBasedGatewaySiblings()` | Event-based gateway spec |
| `FailActivityWithBoundaryCheck()` | Error propagation rules |
| `TryCompleteMultiInstanceHost()` | Multi-instance semantics |
| `PropagateToken()` | Parallel gateway token propagation |

**Root cause:** The grain IS the aggregate root. There is no domain-layer aggregate that encapsulates BPMN execution rules independently of Orleans.

**Target state:** A `WorkflowExecution` domain aggregate that owns execution state and BPMN rules. Grains become thin infrastructure wrappers that delegate to it and handle persistence.

### 2. Anemic Domain Model

Domain objects are primarily data holders:

**Activities:** Most are empty records (`TaskActivity`, `StartEvent`, `EndEvent`) with zero behavior. `Activity.ExecuteAsync()` just calls `activityContext.Execute()` and publishes an event. Real execution logic lives in the grain's `ProcessCommands` switch.

**State classes:** Setters without invariant protection:
- `ActivityInstanceState.Complete()` — sets 3 flags, no validation it wasn't already completed
- `WorkflowInstanceState.AddEntries()` — calls `Entries.AddRange()` without validation
- `GatewayForkState.CreatedTokenIds` — public list directly manipulated by grain code

**Exposed collections:** `State.Entries`, `State.VariableStates`, `State.GatewayForks` are all public `List<T>` properties. Grain code filters/queries them directly (`State.Entries.Where(e => e.ScopeId == ...)`). No aggregate boundary.

---

## P1 — High Issues

### 3. Aggregate Boundary Violations

**`WorkflowInstanceState` is not a proper aggregate root:**
- Doesn't control access to children — grain code, `BoundaryEventHandler`, and event handlers reach directly into collections
- No invariant enforcement (can start twice, complete twice, add invalid entries)

**`BoundaryEventHandler` bypasses the aggregate:**
- `IBoundaryEventStateAccessor` exposes `WorkflowInstanceState State { get; }` directly
- Handler calls `_accessor.State.CompleteEntries(...)` — modifying internals from an application service
- Also drives `TransitionToNextActivity()` and `ExecuteWorkflow()` from outside

**Multiple unguarded entry points for state mutation:**
- `WorkflowInstance.Execution.cs` — adds/completes entries during execution loop
- `WorkflowInstance.ActivityLifecycle.cs` — merges variables, completes entries
- `WorkflowInstance.EventHandling.cs` — modifies state on event delivery
- `BoundaryEventHandler.cs` — completes entries, creates new ones, drives execution
- No single chokepoint enforcing invariants

**Gateway logic fragmented across boundaries:**
- `ExclusiveGateway.ExecuteAsync()` adds conditions to state
- `ConditionalGateway.SetConditionResult()` updates via `IWorkflowExecutionContext`
- `ExclusiveGateway.GetNextActivities()` reads results to decide routing
- Three separate methods — the decision is never atomic

### 4. Infrastructure Concerns in Domain

**Orleans serialization on every domain class:**
- `[GenerateSerializer]` and `[Id(N)]` on all Activity records, State classes, ExecutionCommands
- If persistence changes, domain code must change

**`GrainStorageNames.cs` in `Fleans.Domain`:**
- Storage provider name constants are Orleans infrastructure, not domain concepts

**Execution commands are infrastructure disguised as domain:**
- Activities return `List<IExecutionCommand>` — instructions to the grain, not domain events
- `SpawnActivityCommand`, `RegisterTimerCommand`, etc. describe "what the grain should do"
- Marked with `[GenerateSerializer]` — confirming infrastructure nature
- Proper approach: return domain events that the grain translates to infrastructure actions

**Domain interfaces expose Orleans async patterns:**
- `IActivityExecutionContext` methods return `ValueTask<T>` — async forced by Orleans
- `IWorkflowExecutionContext.GetConditionSequenceStates()` returns raw `ConditionSequenceState[]`
- `IWorkflowExecutionContext.FindForkByToken(Guid)` exposes implementation details

---

## P2 — Moderate Issues

### 5. Missing Value Objects (Primitive Obsession)

| Current | Suggested | Rationale |
|---|---|---|
| `string ActivityId` | `ActivityId` typed wrapper | No validation, easily confused with other strings |
| `Guid variablesId` | `VariablesId` typed wrapper | Confused with `ActivityInstanceId`, `WorkflowInstanceId` |
| `Guid? TokenId` | `Token` value object | Token lifecycle is a domain concept |
| `int? ErrorCode` | `ErrorCode` value object | Only 400/500 valid; classification buried in `Fail()` |
| `string correlationKey` | `CorrelationKey` value object | `MessageCorrelationKey.Build()` is static utility, not VO |

### 6. Missing Domain Events

State changes are silent — no domain events for:
- Workflow lifecycle: `WorkflowStarted`, `WorkflowCompleted`, `WorkflowFailed`
- Activity lifecycle: `ActivityCompleted`, `ActivityFailed`, `ActivityCancelled`
- Structural: `SubProcessCompleted`, `BoundaryEventFired`, `ParallelForkCreated`

The only events that exist (`ExecuteScriptEvent`, `EvaluateConditionEvent`) are infrastructure events for async grain communication. `IDomainEvent` interface exists but is nearly unused for domain state changes. External systems (audit, monitoring) can't subscribe to domain lifecycle.

### 7. Missing Domain Services

- **Error classification** inside `ActivityInstanceState.Fail()` — should be a domain service
- **Variable scope resolution** walks parent chain in `WorkflowInstanceState.GetMergedVariables()` — a `VariableScopeResolver` would be clearer
- **Scope completion detection** scattered across grain methods — should be a `ScopeCompletionPolicy`

### 8. Ambiguous Entity vs Value Object Modeling

- `ConditionSequenceState` has IDs (entity-like) but is mutable with no lifecycle management (DTO-like)
- `ActivityInstanceEntry` is owned by `WorkflowInstanceState` (value object lifecycle) but has its own identity (entity-like)

---

## Dependency Direction Today

```
Api/Web → Application (grains) → Domain (data holders)
                ↑
          Infrastructure (BpmnConverter, scripts)
```

Domain depends on nothing — correct. But domain contains almost no behavior — all BPMN logic is in Application.

## Target Dependency Direction

```
Api/Web → Application (thin grain wrappers) → Domain (aggregates with BPMN logic)
                ↑                                    ↑
          Infrastructure                    Domain Services
          (BpmnConverter, scripts)          (ScopeCompletion, ErrorClassification)
```

Grains delegate to domain aggregates. Domain encapsulates BPMN rules. Infrastructure adapts to domain interfaces.
