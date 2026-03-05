# Test Consolidation Design

## Problem

465 test methods across 79 files (~14,255 LOC). Key pain points:
- **Slow test runs** — full suite takes too long for fast feedback
- **Maintenance burden** — changing one pattern requires updating many near-identical files
- **Hard to navigate** — volume makes it hard to find relevant tests

The worst duplication is in boundary event and intermediate catch event tests, where timer/message/signal variants share 85-97% identical code.

## Approach: Abstract Base Classes

Encode shared test patterns in base classes. Thin subclasses provide event-type-specific setup and triggers. Test count stays at 465 — we reduce code, not coverage.

## Domain Layer

### Boundary Event Domain Tests

**Current:** 3 files with 97% identical code:
- `BoundaryTimerEventDomainTests.cs`
- `MessageBoundaryEventDomainTests.cs`
- `SignalBoundaryEventDomainTests.cs`

**New structure:**
```
BoundaryEventDomainTestBase.cs           (5 shared test methods)
├── BoundaryTimerEventDomainTests.cs     (~15 lines: CreateEvent() override)
├── MessageBoundaryEventDomainTests.cs   (~15 lines: CreateEvent() override)
└── SignalBoundaryEventDomainTests.cs    (~15 lines: CreateEvent() override)
```

Base class provides:
- `abstract Activity CreateBoundaryEvent(...)` — each subclass creates its event type
- `ExecuteAsync_ShouldCompleteImmediately`
- `GetNextActivities_ShouldReturnTarget_ViaSequenceFlow`
- `virtual AssertProperties(Activity)` — subclass asserts event-specific properties
- `IsInterrupting_DefaultsToTrue` / `CanBeSetToFalse`

**LOC reduction:** ~165

### Intermediate Catch Event Domain Tests

**Current:** 3 files with 95% identical code:
- `TimerIntermediateCatchEventDomainTests.cs`
- `MessageIntermediateCatchEventDomainTests.cs`
- `SignalIntermediateCatchEventDomainTests.cs`

**New structure:**
```
CatchEventDomainTestBase.cs                    (2-3 shared test methods)
├── TimerIntermediateCatchEventDomainTests.cs   (~15 lines)
├── MessageIntermediateCatchEventDomainTests.cs (~15 lines)
└── SignalIntermediateCatchEventDomainTests.cs  (~20 lines, extra no-outgoing-flow test)
```

Base class provides:
- `abstract Activity CreateCatchEvent(...)`
- `abstract void AssertCommand(IEnumerable<IExecutionCommand>)` — verify correct command type
- `ExecuteAsync_ShouldCallExecute_RegisterCommand_AndNotComplete`
- `GetNextActivities_ShouldReturnTarget_ViaSequenceFlow`

**LOC reduction:** ~120

## Application Layer

### Boundary Event Integration Tests

**Current:** 3 files with 85% shared code:
- `BoundaryTimerEventTests.cs`
- `MessageBoundaryEventTests.cs`
- `SignalBoundaryEventTests.cs`

**New structure:**
```
BoundaryEventTestBase.cs              (3 shared test scenarios)
├── BoundaryTimerEventTests.cs        (~40 lines)
├── MessageBoundaryEventTests.cs      (~50 lines + stale message test)
└── SignalBoundaryEventTests.cs       (~45 lines + stale signal test)
```

Base class provides:
- `abstract WorkflowDefinition CreateWorkflowWithBoundary()`
- `abstract Task TriggerBoundaryEvent(IWorkflowInstanceGrain, Guid hostInstanceId)`
- `virtual Task SetupInitialState(IWorkflowInstanceGrain)` — no-op default, messages override for correlation vars
- Shared tests:
  - `EventArrivesFirst_ShouldFollowBoundaryFlow`
  - `ActivityCompletesFirst_ShouldFollowNormalFlow`
  - `NonInterrupting_AttachedActivityContinues`

Event-specific tests (stale event handling, subscription cleanup) stay in subclasses.

**`BoundaryOnCatchEventTests.cs`** stays separate — different pattern (boundary on catch event, cross-type scenarios).

**LOC reduction:** ~200

## Gateway Tests

Light touch — extract shared condition setup helpers into `ActivityTestHelper`, keep separate test classes. Gateways have genuinely different semantics (short-circuit vs wait-all vs token propagation).

- Extract `CreateDefinitionWithConditionalFlows` helper
- Extract common condition state setup patterns
- Keep `ExclusiveGatewayActivityTests`, `InclusiveGatewayActivityTests`, `ParallelGatewayActivityTests` as separate classes

**LOC reduction:** ~80

## Out of Scope

- EF Core storage tests (large but not duplicated — test different entity types)
- `WorkflowInstanceTests.cs` (integration scenarios, no duplication pattern)
- Manual test plans in `tests/manual/`
- `BpmnConverter` tests (already use a shared base class)

## Summary

| Area | Files Before | Files After | LOC Reduction |
|------|-------------|-------------|---------------|
| Domain boundary events | 3 | 1 base + 3 thin | ~165 |
| Domain catch events | 3 | 1 base + 3 thin | ~120 |
| App boundary events | 3 | 1 base + 3 thin | ~200 |
| Gateway helpers | 3 | 3 (extract helpers) | ~80 |
| **Total** | **12** | **12 (restructured)** | **~565 LOC** |

Test method count unchanged: 465. All existing test scenarios preserved.
