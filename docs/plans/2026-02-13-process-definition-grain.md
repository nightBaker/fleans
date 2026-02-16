# ProcessDefinitionGrain — Separate grain for workflow definitions

## Problem

`WorkflowInstance` holds `WorkflowDefinition` as an in-memory property that is lost on grain deactivation. Only `ProcessDefinitionId` survives in `WorkflowInstanceState`. After reactivation, the grain has no way to restore its definition.

## Decision

Introduce a read-only `ProcessDefinitionGrain` keyed by `ProcessDefinitionId`. `WorkflowInstance` lazy-loads the definition from this grain on first use after reactivation.

### Why a separate grain (not embedded in WorkflowInstanceState)

- Definitions are immutable and shared across many instances — no need to duplicate per instance.
- Fits the grain-per-entity model: one grain per deployed definition.
- Reuses `EfCoreProcessDefinitionGrainStorage` already in place.

## Design

### ProcessDefinitionGrain

- **Key:** string (`ProcessDefinitionId`)
- **State:** `ProcessDefinition` via `[PersistentState("state", "processDefinitions")]`
- **Interface:** `IProcessDefinitionGrain : IGrainWithStringKey`
  - `Task<WorkflowDefinition> GetDefinition()` — returns `_state.State.Workflow` (not the full `ProcessDefinition`, to avoid sending `BpmnXml` across grain boundaries)
- **Read-only:** Factory writes definitions via `IProcessDefinitionRepository`; the grain only reads.

### WorkflowInstance changes

- Add `GetWorkflowDefinitionAsync()` that returns cached `WorkflowDefinition` or lazy-loads from `ProcessDefinitionGrain`.
- `SetWorkflow()` still sets `WorkflowDefinition` directly on first creation.
- All call sites that read `WorkflowDefinition` switch to `await GetWorkflowDefinitionAsync()`.
- Remove the TODO comment.

### What stays unchanged

- `WorkflowInstanceFactoryGrain` — writes via repository, caches in memory.
- `EfCoreProcessDefinitionRepository` — the write path.
- `WorkflowInstanceState` — already has `ProcessDefinitionId`.

## Files

| File | Action |
|------|--------|
| `Fleans.Application/Grains/IProcessDefinitionGrain.cs` | Create |
| `Fleans.Application/Grains/ProcessDefinitionGrain.cs` | Create |
| `Fleans.Application/Grains/WorkflowInstance.cs` | Modify — lazy loading |
