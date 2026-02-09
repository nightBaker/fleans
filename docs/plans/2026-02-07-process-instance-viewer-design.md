# Process Instance Viewer Design

**Status:** Implemented (2026-02-09)

## Goal

Add a process instance detail page to the Blazor Web app that visualizes a BPMN diagram with highlighted current/completed steps — similar to Camunda Cockpit. Show full activity details, variable state, condition evaluations, and error information.

## Navigation Flow

```
Workflows page → "Instances" button on version row → Instance list → Click instance → Instance detail page
```

## Approach

- **Rendering:** bpmn-js (same library Camunda uses) via Blazor JS interop
- **Communication:** Blazor Server calls `WorkflowEngine` directly (no new API endpoints)
- **BPMN XML:** Stored in `ProcessDefinition` record alongside the parsed `WorkflowDefinition`

## Data Layer

### Store BPMN XML

`ProcessDefinition` record has a `BpmnXml` field (Id 5). The deploy flow threads raw XML through `WorkflowEngine.DeployWorkflow` → `IWorkflowInstanceFactoryGrain.DeployWorkflow`.

### Track Instances Per Process Key

`WorkflowInstanceFactoryGrain` tracks:
- `_instancesByKey`: processDefinitionKey → instance IDs
- `_instanceToDefinitionId`: instanceId → processDefinitionId

### Expose Instance State — Enriched Snapshot

`IWorkflowInstanceState.GetStateSnapshot()` returns a rich `InstanceStateSnapshot`:

```csharp
record InstanceStateSnapshot(
    List<string> ActiveActivityIds,           // Id 0 — flat IDs for BPMN diagram JS interop
    List<string> CompletedActivityIds,        // Id 1
    bool IsStarted,                           // Id 2
    bool IsCompleted,                         // Id 3
    List<ActivityInstanceSnapshot> ActiveActivities,     // Id 4 — detailed snapshots
    List<ActivityInstanceSnapshot> CompletedActivities,  // Id 5
    List<VariableStateSnapshot> VariableStates,          // Id 6
    List<ConditionSequenceSnapshot> ConditionSequences); // Id 7
```

Supporting DTOs:

```csharp
record ActivityInstanceSnapshot(
    Guid ActivityInstanceId,    // Id 0
    string ActivityId,          // Id 1
    string ActivityType,        // Id 2 — e.g. "StartEvent", "TaskActivity", "ScriptTask"
    bool IsCompleted,           // Id 3
    bool IsExecuting,           // Id 4
    Guid VariablesStateId,      // Id 5
    ActivityErrorState? ErrorState,  // Id 6
    DateTimeOffset? CompletedAt);    // Id 7

record VariableStateSnapshot(
    Guid VariablesId,                      // Id 0
    Dictionary<string, string> Variables); // Id 1 — values serialized via ToString()

record ConditionSequenceSnapshot(
    string SequenceFlowId,      // Id 0
    string Condition,           // Id 1
    string SourceActivityId,    // Id 2
    string TargetActivityId,    // Id 3
    bool Result);               // Id 4
```

### Activity Completion Tracking

`ActivityInstance` records `DateTimeOffset? _completedAt` in `Complete()`. Since `Fail()` delegates to `Complete()`, failed activities also have a completion timestamp.

### GetSnapshot() Batching

`IActivityInstance.GetSnapshot()` returns a full `ActivityInstanceSnapshot` in a single grain call, reading all fields from local grain state. `WorkflowInstanceState.GetStateSnapshot()` fans out `GetSnapshot()` calls to all activity grains via `Task.WhenAll`, reducing grain calls from 7N to N (where N = number of activities).

### Orleans Concurrency

All read-only methods on `IWorkflowInstanceState` are annotated with `[ReadOnly]` to allow concurrent reads without write locks. This includes `GetStateSnapshot()`, `GetActiveActivities()`, `GetCompletedActivities()`, `IsStarted()`, `IsCompleted()`, etc.

`WorkflowEngine` methods:
- `GetInstancesByKey(string key)` — lists instances for a process key
- `GetInstanceDetail(Guid instanceId)` — returns enriched `InstanceStateSnapshot`
- `GetBpmnXml(Guid instanceId)` — returns BPMN XML for diagram rendering

## UI Layer — ProcessInstance.razor

Route: `/process-instance/{InstanceId:guid}`

### Layout (top to bottom)

1. **PageHeader** with status badge (Completed/Running)
2. **Instance ID**
3. **BPMN diagram** — bpmn-js canvas with completed (green) and active (blue) highlighting
4. **Refresh button** — with `Loading` parameter for loading state, includes error handling
5. **Error banners** — `FluentMessageBar` per failed activity
6. **FluentTabs** with 3 tabs:

### Activities Tab

`FluentDataGrid` with columns:
- **Activity ID** — `PropertyColumn`, sortable, filterable via `FluentSearch`
- **Type** — `PropertyColumn`, sortable, filterable
- **Status** — `TemplateColumn` with colored `FluentBadge` (Failed/Completed/Running/Pending), sortable via `GridSort`, filterable
- **Completed At** — `PropertyColumn`, sortable, formatted `yyyy-MM-dd HH:mm:ss`
- **Error** — `TemplateColumn` showing `[code] message` or `-`, sortable, filterable

### Variables Tab

Per `VariableStateSnapshot`: label with truncated ID, then `FluentDataGrid` with:
- **Key** — `PropertyColumn`, sortable
- **Value** — `PropertyColumn`, sortable

If empty: info message.

### Conditions Tab

`FluentDataGrid` with columns:
- **Sequence Flow** — `PropertyColumn`, sortable, filterable
- **Condition** — `PropertyColumn`, sortable, filterable
- **Path** — `TemplateColumn` showing `source → target`, sortable via `GridSort`, filterable
- **Result** — `TemplateColumn` with True/False `FluentBadge`, sortable, filterable via `FluentSelect` dropdown (All/True/False)

If empty: info message.

### Filtering Architecture

Filters use `ColumnOptions` child content with `FluentSearch` (text) or `FluentSelect` (dropdown). Filter state is maintained in `@code` fields (`activityIdFilter`, `activityTypeFilter`, etc.). `ApplyActivityFilters()` and `ApplyConditionFilters()` methods rebuild `IQueryable` from the full list on each filter change.

## File Changes

| File | Change |
|------|--------|
| `Fleans.Domain/ProcessDefinitions.cs` | `InstanceStateSnapshot` extended with 4 new properties (Id 4-7), 3 new snapshot records |
| `Fleans.Domain/IActivityInstance.cs` | Added `GetSnapshot()`, `GetCompletedAt()` |
| `Fleans.Domain/ActivityInstance.cs` | Implemented `GetSnapshot()`, `GetCompletedAt()`, `_completedAt` tracking |
| `Fleans.Domain/States/IWorkflowInstanceState.cs` | `[ReadOnly]` on all read methods |
| `Fleans.Domain/States/WorkflowInstanceState.cs` | `GetStateSnapshot()` uses `GetSnapshot()` batching, builds enriched DTOs |
| `Fleans.Web/Components/Pages/ProcessInstance.razor` | Tabbed UI with FluentDataGrid, sorting, filtering, error banners |

## Test Coverage

### TaskActivityTests (5 tests)
- `GetCompletedAt` returns null before completion, timestamp after completion, timestamp after failure
- `GetSnapshot` returns all fields in one call, includes error state after failure

### WorkflowInstanceStateTests (4 tests)
- `GetStateSnapshot` returns active/completed activity snapshots with correct types and timestamps
- `GetStateSnapshot` serializes variables to `Dictionary<string, string>`
- `GetStateSnapshot` returns condition sequences with correct paths and results
- `GetStateSnapshot` with empty state returns empty lists

## Not In Scope

- Persistence (state remains in-memory)
- Real-time updates / auto-refresh
- Variable editing from the UI
- Complex object serialization for variables (uses `ToString()`)
