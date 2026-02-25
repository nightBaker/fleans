# Extract EventBasedGateway Sibling Query to Domain

**Date:** 2026-02-25
**Status:** Approved

## Problem

`WorkflowInstance.CancelEventBasedGatewaySiblings` contains a domain query (find sibling catch events from an EventBasedGateway) mixed with infrastructure work (unsubscribe from correlation grains, cancel activity instances). The domain query should live on `IWorkflowDefinition`.

Additionally, the current implementation searches only root-level `definition.SequenceFlows`, so it wouldn't find EventBasedGateways inside SubProcesses. The extracted method fixes this by using `FindScopeForActivity` to search the correct scope.

## Design

Add a default interface method to `IWorkflowDefinition`:

```csharp
IReadOnlySet<string> GetEventBasedGatewaySiblings(string completedActivityId)
```

Returns sibling activity IDs for catch events competing with `completedActivityId` after an EventBasedGateway, or empty set if not applicable. Uses `FindScopeForActivity` for correct scope resolution.

### Changes

1. **`IWorkflowDefinition`** — add default interface method `GetEventBasedGatewaySiblings`
2. **`WorkflowInstance.CancelEventBasedGatewaySiblings`** — replace inline LINQ queries (lines 329-339) with `definition.GetEventBasedGatewaySiblings(completedActivityId)`. Infrastructure foreach loop unchanged.
3. **Tests** — unit tests for the domain method covering: activity after gateway, activity not after gateway, multiple siblings, gateway inside SubProcess.
