# Design: Migrate ParallelGateway to Token Propagation

**Date:** 2026-03-04
**Status:** Approved
**Goal:** Unify ParallelGateway and InclusiveGateway join detection under a shared token-based mechanism, replacing the static graph-analysis approach in ParallelGateway.

## Problem

ParallelGateway and InclusiveGateway both implement fork/join semantics but use different join detection:

- **ParallelGateway** uses `AllIncomingPathsCompleted()` — static graph analysis that checks if every incoming source activity has a completed instance. This is fragile with nested forks and loops because it matches by activity ID, not by specific fork instance.
- **InclusiveGateway** uses `AllExpectedTokensArrived()` — dynamic token counting that matches tokens created by the specific fork to tokens that arrived at the join. Correct by construction for any topology.

The token-based approach is strictly superior. Both gateways should use it.

## Design

### Class Hierarchy (before → after)

**Before:**
```
Gateway
  ├── ParallelGateway (IsFork, AllIncomingPathsCompleted)
  └── ConditionalGateway (SetConditionResult)
        ├── ExclusiveGateway
        └── InclusiveGateway (IsFork, AllExpectedTokensArrived)
```

**After:**
```
Gateway
  └── ConditionalGateway (SetConditionResult)
        ├── ExclusiveGateway (unchanged)
        └── ForkJoinGateway (IsFork, AllExpectedTokensArrived)
              ├── ParallelGateway (fork: all paths)
              └── InclusiveGateway (fork: conditional paths)
```

### New Class: `ForkJoinGateway`

Abstract record extending `ConditionalGateway`. Contains the shared fork/join token infrastructure:

- **Property** `bool IsFork` — distinguishes fork (split) from join (merge) instances
- **Property** `IsJoinGateway => !IsFork`
- **Method** `AllExpectedTokensArrived(workflowContext, definition)` — extracted from InclusiveGateway unchanged. Collects tokens from completed source activities, finds the matching `GatewayForkState`, checks all created tokens have arrived.

Subclasses override `ExecuteAsync` and `GetNextActivities` for their fork-specific behavior (all paths vs conditional paths) but share the join logic.

### ParallelGateway Changes

- Extends `ForkJoinGateway(ActivityId, IsFork)` instead of `Gateway(ActivityId)`
- **Fork `GetNextActivities`**: Returns all outgoing flows with `CloneVariables: true` AND `Token: TokenAction.CreateNew` (previously no token action)
- **Join `ExecuteAsync`**: Calls `AllExpectedTokensArrived()` instead of `AllIncomingPathsCompleted()`
- **Join `GetNextActivities`**: Returns outgoing flows with `Token: TokenAction.RestoreParent` (previously no token action)
- **Delete** `AllIncomingPathsCompleted()` — replaced by inherited token-based join

### InclusiveGateway Changes

- Extends `ForkJoinGateway(ActivityId, IsFork)` instead of `ConditionalGateway(ActivityId)`
- **Delete** `AllExpectedTokensArrived()` — now inherited from `ForkJoinGateway`
- Everything else unchanged (condition evaluation, fork transitions, `SetConditionResult` override)

### Application Layer

**Zero changes.** `ForkJoinGateway : ConditionalGateway`, so the `as ConditionalGateway` cast in `WorkflowInstance.ActivityLifecycle.cs:273` continues to work for InclusiveGateway. Token propagation in `PropagateToken` already handles `TokenAction.CreateNew` and `TokenAction.RestoreParent`.

### Trade-off

ParallelGateway becomes a `ConditionalGateway` by inheritance. This is technically misleading — ParallelGateway doesn't evaluate conditions. However, it's **harmless**: ParallelGateway never produces `AddConditionsCommand`, so `SetConditionResult` is never called on it. The alternative (breaking InclusiveGateway out of ConditionalGateway) would require application-layer changes for no behavioral benefit.

## Behavioral Equivalence

For ParallelGateway (all branches always taken), the token-based join is equivalent to the static graph join:

- Fork creates N tokens (one per outgoing flow) — same as N branches
- Join waits for all N tokens — same as waiting for all source activities
- The difference matters only in topologies where not all branches are taken (loops, nested conditional forks), where the token-based approach is more correct

## Test Impact

- All existing ParallelGateway and InclusiveGateway tests should pass without modification
- Behavior is equivalent for all current test scenarios
