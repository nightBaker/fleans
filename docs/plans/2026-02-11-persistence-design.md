# Persistence Design: Orleans IGrainStorage

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Persist grain state through Orleans `IGrainStorage` providers. Extract `ActivityInstanceState` POCO. Persist `ProcessDefinition` separately outside Orleans grain storage.

**Architecture:** Two grains (`WorkflowInstance`, `ActivityInstance`) use `IPersistentState<T>` with custom `IGrainStorage` providers. `ProcessDefinition` is persisted via a dedicated `IProcessDefinitionRepository` — not through Orleans grain storage — because the factory grain holds a collection of definitions, not a single state object.

**Tech Stack:** Orleans 9.2.1 (`IGrainStorage`, `IPersistentState<T>`), .NET 10

---

## Design Decisions

**No `ICommandRepository<T>`, `IAggregateRoot`, `IUnitOfWork`.**
`IGrainStorage` is a key-value store (read/write by grain ID). A query-oriented repository pattern doesn't fit. Storage providers implement `IGrainStorage` directly — no intermediate abstraction.

**ProcessDefinition persisted separately.**
`WorkflowInstanceFactoryGrain` is a singleton holding 4 dictionaries of multiple definitions. This doesn't fit `IPersistentState<T>` (one state per grain). Instead, `IProcessDefinitionRepository` is a standalone interface the factory grain calls directly.

**`WriteStateAsync()` called after every state mutation.**
Orleans doesn't auto-persist. Every grain method that mutates state must call `await _state.WriteStateAsync()` at the end.

**Grain reference serialization deferred.**
`WorkflowInstanceState` holds `List<IActivityInstance>` grain references. These can't be serialized to a database directly. For now, `IGrainStorage` implementations are abstract (no DB). A future PR will address mapping grain references to `Guid` IDs for storage.

**Concurrency control deferred.**
Orleans `IGrainState<T>` has `ETag` for optimistic concurrency. A future PR will add version fields to state classes.

**No database implementation in this PR.**
Storage providers compile but use in-memory or no-op implementations. A future PR adds EF Core or other concrete storage.

---

## Persistence Mapping

| Grain | State | Persistence Mechanism |
|-------|-------|----------------------|
| `WorkflowInstanceFactoryGrain` | Multiple `ProcessDefinition` records | `IProcessDefinitionRepository` (direct call) |
| `WorkflowInstance` | `WorkflowInstanceState` | `IPersistentState<T>` + `IGrainStorage` |
| `ActivityInstance` | `ActivityInstanceState` (new) | `IPersistentState<T>` + `IGrainStorage` |

---

## IProcessDefinitionRepository

Dedicated interface for process definition persistence. Called directly by `WorkflowInstanceFactoryGrain`, not through Orleans grain storage.

```csharp
// Fleans.Domain/Persistence/IProcessDefinitionRepository.cs
public interface IProcessDefinitionRepository
{
    Task<ProcessDefinition?> GetByIdAsync(string processDefinitionId);
    Task<List<ProcessDefinition>> GetByKeyAsync(string processDefinitionKey);
    Task<List<ProcessDefinition>> GetAllAsync();
    Task SaveAsync(ProcessDefinition definition);
    Task DeleteAsync(string processDefinitionId);
}
```

The factory grain calls this on deploy:
```csharp
await _repository.SaveAsync(definition);
```

And rehydrates on activation:
```csharp
public override async Task OnActivateAsync(CancellationToken ct)
{
    var all = await _repository.GetAllAsync();
    foreach (var def in all)
    {
        _byId[def.ProcessDefinitionId] = def;
        // ... rebuild other dictionaries
    }
}
```

---

## ActivityInstanceState Extraction

Extract from `ActivityInstance` grain fields into new POCO (same pattern as `WorkflowInstanceState`):

```csharp
// Fleans.Domain/States/ActivityInstanceState.cs
public class ActivityInstanceState
{
    private Activity? _currentActivity;
    private bool _isExecuting;
    private bool _isCompleted;
    private Guid _variablesId;
    private ActivityErrorState? _errorState;
    private DateTimeOffset? _createdAt;
    private DateTimeOffset? _executionStartedAt;
    private DateTimeOffset? _completedAt;

    // Getters
    public Activity? GetCurrentActivity() => _currentActivity;
    public bool IsCompleted() => _isCompleted;
    public bool IsExecuting() => _isExecuting;
    public Guid GetVariablesId() => _variablesId;
    public ActivityErrorState? GetErrorState() => _errorState;
    public DateTimeOffset? CreatedAt => _createdAt;
    public DateTimeOffset? ExecutionStartedAt => _executionStartedAt;
    public DateTimeOffset? CompletedAt => _completedAt;

    // Mutations
    public void Complete()
    {
        _isExecuting = false;
        _isCompleted = true;
        _completedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(Exception exception)
    {
        if (exception is ActivityException activityException)
            _errorState = activityException.GetActivityErrorState();
        else
            _errorState = new ActivityErrorState(500, exception.Message);
    }

    public void Execute()
    {
        _errorState = null;
        _isCompleted = false;
        _isExecuting = true;
        _executionStartedAt = DateTimeOffset.UtcNow;
    }

    public void SetActivity(Activity activity)
    {
        _currentActivity = activity;
        _createdAt = DateTimeOffset.UtcNow;
    }

    public void SetVariablesId(Guid id) => _variablesId = id;

    public ActivityInstanceSnapshot GetSnapshot(Guid grainId) =>
        new(grainId, _currentActivity!.ActivityId, _currentActivity.GetType().Name,
            _isCompleted, _isExecuting, _variablesId, _errorState,
            _createdAt, _executionStartedAt, _completedAt);
}
```

`ActivityInstance` grain becomes a thin wrapper:

```csharp
public partial class ActivityInstance : Grain, IActivityInstance
{
    private readonly IPersistentState<ActivityInstanceState> _state;
    private readonly ILogger<ActivityInstance> _logger;

    public ActivityInstance(
        [PersistentState("state", "activityInstances")] IPersistentState<ActivityInstanceState> state,
        ILogger<ActivityInstance> logger)
    {
        _state = state;
        _logger = logger;
    }

    private ActivityInstanceState State => _state.State;

    // All methods delegate to State, then call _state.WriteStateAsync()
    // RequestContext.Set stays in grain (Orleans infrastructure)
    // PublishEvent stays in grain (needs GrainFactory)
    // Logging stays in grain
}
```

---

## IGrainStorage Providers

Two abstract providers in `Fleans.Infrastructure/Storage/`:

```csharp
// Fleans.Infrastructure/Storage/WorkflowInstanceGrainStorage.cs
public class WorkflowInstanceGrainStorage : IGrainStorage
{
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        // No-op for now — state starts empty on activation
        return Task.CompletedTask;
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        // No-op for now — future PR adds DB write
        return Task.CompletedTask;
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        // No-op for now
        return Task.CompletedTask;
    }
}

// Fleans.Infrastructure/Storage/ActivityInstanceGrainStorage.cs
public class ActivityInstanceGrainStorage : IGrainStorage
{
    // Same no-op pattern
}
```

Registration in silo config:

```csharp
siloBuilder
    .AddGrainStorage("workflowInstances", (sp, name) =>
        new WorkflowInstanceGrainStorage())
    .AddGrainStorage("activityInstances", (sp, name) =>
        new ActivityInstanceGrainStorage());
```

---

## WorkflowInstance Changes

```csharp
public partial class WorkflowInstance : Grain, IWorkflowInstance
{
    private readonly IPersistentState<WorkflowInstanceState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowInstance> _logger;

    public WorkflowInstance(
        [PersistentState("state", "workflowInstances")] IPersistentState<WorkflowInstanceState> state,
        IGrainFactory grainFactory,
        ILogger<WorkflowInstance> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    private WorkflowInstanceState State => _state.State;

    // Every mutation method ends with:
    // await _state.WriteStateAsync();
}
```

Example — `CompleteActivity` with `WriteStateAsync`:

```csharp
public async Task CompleteActivity(string activityId, ExpandoObject variables)
{
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();
    LogCompletingActivity(activityId);
    await CompleteActivityState(activityId, variables);
    await ExecuteWorkflow();
    await _state.WriteStateAsync();
}
```

---

## WorkflowInstanceFactoryGrain Changes

```csharp
public partial class WorkflowInstanceFactoryGrain : Grain, IWorkflowInstanceFactoryGrain
{
    private readonly IProcessDefinitionRepository _repository;
    // ... existing dictionaries stay

    public WorkflowInstanceFactoryGrain(
        IProcessDefinitionRepository repository,
        IGrainFactory grainFactory,
        ILogger<WorkflowInstanceFactoryGrain> logger)
    {
        _repository = repository;
        // ...
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        var all = await _repository.GetAllAsync();
        foreach (var def in all)
        {
            _byId[def.ProcessDefinitionId] = def;
            if (!_byKey.TryGetValue(def.ProcessDefinitionKey, out var versions))
            {
                versions = new List<ProcessDefinition>();
                _byKey[def.ProcessDefinitionKey] = versions;
            }
            versions.Add(def);
        }
    }

    // DeployWorkflow adds to dictionaries AND calls:
    // await _repository.SaveAsync(definition);
}
```

---

## File Structure

```
src/Fleans/
├── Fleans.Domain/
│   ├── Persistence/
│   │   └── IProcessDefinitionRepository.cs
│   ├── States/
│   │   ├── WorkflowInstanceState.cs         # Unchanged
│   │   └── ActivityInstanceState.cs         # New POCO
│   └── ActivityInstance.cs                  # Refactor to own ActivityInstanceState
│
├── Fleans.Persistence.InMemory/            # Separate class library
│   ├── WorkflowInstanceGrainStorage.cs     # IGrainStorage (no-op)
│   ├── ActivityInstanceGrainStorage.cs     # IGrainStorage (no-op)
│   ├── InMemoryProcessDefinitionRepository.cs
│   └── DependencyInjection.cs              # AddInMemoryPersistence()
```

---

## Known TODOs (for future PRs)

- `WorkflowInstance.WorkflowDefinition` is not part of persisted state — must move to `WorkflowInstanceState`
- `_instancesByKey` and `_instanceToDefinitionId` in factory grain are not persisted/rehydrated
- State classes (`ActivityInstanceState`, `WorkflowInstanceState`) need `[GenerateSerializer]` and `[Id]` attributes
- Grain reference serialization (`List<IActivityInstance>` in `WorkflowInstanceState`)
- Concurrency control (ETag/versioning)

---

## Not In Scope

- No database implementation (EF Core, SQL, etc.)
- No grain reference serialization (IActivityInstance lists in WorkflowInstanceState)
- No concurrency control (ETag/versioning)
- IGrainStorage providers are no-op placeholders
- `InMemoryProcessDefinitionRepository` is the only repository implementation
