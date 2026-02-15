# CQRS: WorkflowCommandService + WorkflowQueryService

**Date:** 2026-02-15
**Status:** Proposed

## Motivation

The current `WorkflowEngine` facade mixes reads and writes. Read operations (e.g., `GetStateSnapshot`) activate grains and perform N+1 grain-to-grain calls to assemble responses. This causes:

1. **Performance** — `GetStateSnapshot` calls each `IActivityInstance` grain individually to collect snapshots, when all data is already persisted in EF Core tables.
2. **No CQRS separation** — Grains handle both commands and queries, making it hard to optimize either path independently.
3. **Wasted grain activations** — Read-only queries activate grains that consume silo memory for no write-side benefit.

## Design

Replace `WorkflowEngine` with two services:

- **`IWorkflowCommandService`** — Write operations, delegates to Orleans grains.
- **`IWorkflowQueryService`** — Read operations, queries EF Core directly with `AsNoTracking()`.

### Interface Locations

| Artifact | Project |
|---|---|
| `IWorkflowCommandService` (interface) | `Fleans.Application` |
| `WorkflowCommandService` (implementation) | `Fleans.Application` |
| `IWorkflowQueryService` (interface) | `Fleans.Application` |
| `WorkflowQueryService` (implementation) | `Fleans.Persistence` |
| Snapshot DTOs | `Fleans.Application` (moved from `Fleans.Domain`) |

`Fleans.Persistence` references `Fleans.Application` for the interface — standard Clean Architecture direction (outer depends on inner).

### IWorkflowCommandService

```csharp
public interface IWorkflowCommandService
{
    Task<Guid> StartWorkflow(string workflowId);
    Task<Guid> StartWorkflowByProcessDefinitionId(string processDefinitionId);
    void CompleteActivity(Guid workflowInstanceId, string activityId, ExpandoObject variables);
    Task RegisterWorkflow(IWorkflowDefinition workflow);
    Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml);
}
```

Implementation: Extracts the current write methods from `WorkflowEngine`. Still uses `IGrainFactory` to call grains.

### IWorkflowQueryService

```csharp
public interface IWorkflowQueryService
{
    Task<InstanceStateSnapshot> GetStateSnapshot(Guid workflowInstanceId);
    Task<IReadOnlyList<ProcessDefinitionSummary>> GetAllProcessDefinitions();
    Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey);
    Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string processDefinitionKey, int version);
    Task<string?> GetBpmnXml(Guid instanceId);
    Task<string?> GetBpmnXmlByKey(string processDefinitionKey);
    Task<string?> GetBpmnXmlByKeyAndVersion(string processDefinitionKey, int version);
}
```

Note: `GetAllWorkflows()` (returning `WorkflowSummary`) is removed — it returns in-memory workflow definitions that aren't persisted. `GetAllProcessDefinitions()` already covers the deployed/persisted definitions.

### WorkflowQueryService Implementation (Persistence)

Uses `FleanDbContext` with `AsNoTracking()`. The key query — `GetStateSnapshot` — replaces the current N+1 grain calls:

```csharp
public async Task<InstanceStateSnapshot> GetStateSnapshot(Guid workflowInstanceId)
{
    var state = await _dbContext.WorkflowInstances
        .Include(w => w.Entries)
        .Include(w => w.VariableStates)
        .Include(w => w.ConditionSequenceStates)
        .AsNoTracking()
        .FirstOrDefaultAsync(w => w.Id == workflowInstanceId);

    // Load ActivityInstanceState for each entry
    var activityInstanceIds = state.Entries.Select(e => e.ActivityInstanceId).ToList();
    var activityInstances = await _dbContext.ActivityInstances
        .Where(a => activityInstanceIds.Contains(a.Id))
        .AsNoTracking()
        .ToListAsync();

    // For condition enrichment, load the process definition to get sequence flow metadata
    var processDefinitionId = state.ProcessDefinitionId;
    var processDefinition = await _dbContext.ProcessDefinitions
        .AsNoTracking()
        .FirstOrDefaultAsync(p => p.ProcessDefinitionId == processDefinitionId);

    // Project into snapshot DTOs...
}
```

The condition sequence enrichment (adding `Condition`, `SourceActivityId`, `TargetActivityId`) deserializes the `WorkflowDefinition` from `ProcessDefinition.Workflow` and looks up `ConditionalSequenceFlow` definitions by ID — same logic as the current grain implementation, just sourced from the DB.

Other query methods (`GetAllProcessDefinitions`, `GetInstancesByKey`, etc.) are straightforward single-table queries projecting into existing DTOs.

### Snapshot DTOs — Move to Application

These records move from `Fleans.Domain` to `Fleans.Application`:

- `InstanceStateSnapshot`
- `ActivityInstanceSnapshot`
- `VariableStateSnapshot`
- `ConditionSequenceSnapshot`
- `WorkflowInstanceInfo`
- `ProcessDefinitionSummary`
- `WorkflowSummary` (or remove entirely if `GetAllWorkflows` is dropped)
- `ActivityErrorState` (if only used in snapshots)

The `[GenerateSerializer]` attributes are removed since these DTOs no longer cross grain boundaries. They become plain records.

### Grain Interface Cleanup

**Remove from `IWorkflowInstance`:**
- `GetStateSnapshot()` — replaced by query service
- `GetInstanceInfo()` — replaced by query service
- `GetWorkflowInstanceId()` — only used by `WorkflowEngine`, command service can get ID differently
- `GetCreatedAt()`, `GetExecutionStartedAt()`, `GetCompletedAt()` — query service reads timestamps from DB

**Remove from `IWorkflowInstanceFactoryGrain`:**
- `GetAllWorkflows()` — not persisted, dropped
- `GetAllProcessDefinitions()` — query service reads from DB
- `GetInstancesByKey()` / `GetInstancesByKeyAndVersion()` — query service reads from DB
- `GetBpmnXml()` / `GetBpmnXmlByKey()` / `GetBpmnXmlByKeyAndVersion()` / `GetBpmnXmlByInstanceId()` — query service reads from DB

**Keep on `IWorkflowInstance`:**
- `StartWorkflow()`
- `SetWorkflow()`
- `CompleteActivity()`
- `FailActivity()`
- `CompleteConditionSequence()`
- `Complete()`
- `GetWorkflowDefinition()` — needed internally by grain execution
- `GetVariables()` — needed internally by activity execution
- `GetActiveActivities()` / `GetCompletedActivities()` — needed internally by grain orchestration
- `GetConditionSequenceStates()` / `AddConditionSequenceStates()` / `SetConditionSequenceResult()` — needed internally by gateway logic

**Keep on `IWorkflowInstanceFactoryGrain`:**
- `CreateWorkflowInstanceGrain()` / `CreateWorkflowInstanceGrainByProcessDefinitionId()`
- `DeployWorkflow()`
- `RegisterWorkflow()` / `IsWorkflowRegistered()`

### Consumer Migration

#### Fleans.Web (Blazor Server)

| Component | Current | After |
|---|---|---|
| `Workflows.razor` | `WorkflowEngine` | `IWorkflowQueryService` + `IWorkflowCommandService` |
| `ProcessInstances.razor` | `WorkflowEngine` | `IWorkflowQueryService` |
| `ProcessInstance.razor` | `WorkflowEngine` | `IWorkflowQueryService` |
| `Editor.razor` | `WorkflowEngine` | `IWorkflowQueryService` + `IWorkflowCommandService` |

#### Fleans.Api

| Endpoint | Current | After |
|---|---|---|
| `POST /start` | `WorkflowEngine` | `IWorkflowCommandService` |
| `POST /upload-bpmn` | `WorkflowEngine` | `IWorkflowCommandService` |
| `POST /register` | `WorkflowEngine` | `IWorkflowCommandService` |
| `GET /workflows` | `WorkflowEngine` | `IWorkflowQueryService` |

### DI Registration

```csharp
// In ApplicationDependencyInjection.cs
public static void AddApplication(this IServiceCollection services)
{
    services.AddSingleton<IWorkflowCommandService, WorkflowCommandService>();
}

// In PersistenceDependencyInjection.cs (or new extension method)
public static void AddQueryServices(this IServiceCollection services)
{
    services.AddScoped<IWorkflowQueryService, WorkflowQueryService>();
}
```

`WorkflowQueryService` is registered as **Scoped** (tied to DbContext lifetime), not Singleton.

### Delete

- `Fleans.Application/WorkflowEngine.cs` — replaced entirely
- `GetStateSnapshot()` method on `WorkflowInstance` grain
- `GetSnapshot()` on `ActivityInstance` grain (if no longer used internally)
- Read-only methods from `WorkflowInstanceFactoryGrain` implementation
- Snapshot DTO files from `Fleans.Domain/Definitions/` (moved to Application)

## Testing

**New: `WorkflowQueryService` integration tests**
- Use in-memory SQLite (or existing EF Core test setup)
- Seed DB with `WorkflowInstanceState` + related entries, `ActivityInstanceState`, `WorkflowVariablesState`, `ConditionSequenceState`, `ProcessDefinition`
- Assert `GetStateSnapshot` returns correctly projected DTOs
- Test edge cases: no active activities, no variables, no conditions, completed vs running instances
- Test `GetAllProcessDefinitions`, `GetInstancesByKey`, `GetBpmnXml*` methods

**Existing grain tests:**
- Unaffected — they test workflow execution (write path) via `workflowInstance.GetState()`, not `GetStateSnapshot`

**Remove:**
- Any tests that call `GetStateSnapshot` through the grain interface
