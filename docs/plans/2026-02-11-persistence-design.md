# Persistence Design: ICommandRepository + Orleans IGrainStorage

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add abstract persistence interfaces and wire grain state through Orleans `IGrainStorage` providers that delegate to `ICommandRepository<T>`.

**Architecture:** Three aggregate roots (`ProcessDefinition`, `WorkflowInstanceState`, `ActivityInstanceState`) each get a dedicated `IGrainStorage` provider. Providers delegate to `ICommandRepository<T>` — no concrete DB implementation yet.

**Tech Stack:** Orleans 9.2.1 (`IGrainStorage`, `IPersistentState<T>`), .NET 10

---

## Design Decisions

**Reuse existing classes as aggregate roots.**
`ProcessDefinition` and `WorkflowInstanceState` already exist. Add `IAggregateRoot` marker interface. Only `ActivityInstanceState` is new (extracted from `ActivityInstance` grain, same pattern as `WorkflowInstanceState` extraction).

**Domain layer owns interfaces only.**
`IAggregateRoot`, `IUnitOfWork`, `ICommandRepository<T>` live in `Fleans.Domain/Persistence/`. No database dependencies in Domain.

**Infrastructure layer owns IGrainStorage implementations.**
Three providers in `Fleans.Infrastructure/Storage/`, one per aggregate. Each takes `ICommandRepository<T>` via constructor.

**Orleans `IPersistentState<T>` for grain state.**
Grains receive state via `[PersistentState("state", "providerName")]` constructor injection. Existing code accesses `State.X` unchanged — only the initialization path changes.

**No database implementation in this PR.**
Storage providers compile but aren't wired up. A future PR adds EF Core (or other) `ICommandRepository<T>` implementations.

---

## Aggregate Root Mapping

| Grain | Aggregate Root | Approach |
|-------|---------------|----------|
| `WorkflowInstanceFactoryGrain` | `ProcessDefinition` | Add `IAggregateRoot` to existing record |
| `WorkflowInstance` | `WorkflowInstanceState` | Add `IAggregateRoot` to existing class |
| `ActivityInstance` | `ActivityInstanceState` (new) | Extract state fields into POCO |

---

## Interfaces

### IAggregateRoot

```csharp
// Fleans.Domain/Persistence/IAggregateRoot.cs
public interface IAggregateRoot { }
```

### IUnitOfWork

```csharp
// Fleans.Domain/Persistence/IUnitOfWork.cs
public interface IUnitOfWork : IDisposable
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
```

### ICommandRepository

```csharp
// Fleans.Domain/Persistence/ICommandRepository.cs
public interface ICommandRepository<T> where T : IAggregateRoot
{
    IUnitOfWork UnitOfWork { get; }
    Task<T> GetAsync(Expression<Func<T, bool>> predicate);
    void Add(T item);
    T Remove(T item);
    Task<List<T>> GetListAsync(Expression<Func<T, bool>> predicate);
}
```

---

## ActivityInstanceState Extraction

Extract from `ActivityInstance` grain fields into new POCO:

```csharp
// Fleans.Domain/States/ActivityInstanceState.cs
public class ActivityInstanceState : IAggregateRoot
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
    private ActivityInstanceState State { get; } = new();
    private readonly ILogger<ActivityInstance> _logger;

    // All methods delegate to State
    // RequestContext.Set stays in grain (Orleans infrastructure)
    // PublishEvent stays in grain (needs GrainFactory)
    // Logging stays in grain
}
```

---

## IGrainStorage Providers

Three providers in `Fleans.Infrastructure/Storage/`, each delegating to `ICommandRepository<T>`:

```csharp
public class WorkflowInstanceGrainStorage : IGrainStorage
{
    private readonly ICommandRepository<WorkflowInstanceState> _repository;

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) { ... }
    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) { ... }
    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) { ... }
}
```

Registration:

```csharp
siloBuilder
    .AddGrainStorage("processDefinitions", (sp, name) =>
        new ProcessDefinitionGrainStorage(sp.GetRequiredService<ICommandRepository<ProcessDefinition>>()))
    .AddGrainStorage("workflowInstances", ...)
    .AddGrainStorage("activityInstances", ...);
```

Grain usage:

```csharp
public class WorkflowInstance : Grain, IWorkflowInstance
{
    private readonly IPersistentState<WorkflowInstanceState> _state;

    public WorkflowInstance(
        [PersistentState("state", "workflowInstances")] IPersistentState<WorkflowInstanceState> state, ...)
    {
        _state = state;
    }

    private WorkflowInstanceState State => _state.State;
    // All existing State.X calls work unchanged
}
```

---

## File Structure

```
src/Fleans/
├── Fleans.Domain/
│   ├── Persistence/
│   │   ├── IAggregateRoot.cs
│   │   ├── IUnitOfWork.cs
│   │   └── ICommandRepository.cs
│   ├── States/
│   │   ├── WorkflowInstanceState.cs      # Add : IAggregateRoot
│   │   └── ActivityInstanceState.cs      # New POCO
│   ├── ProcessDefinitions.cs             # Add : IAggregateRoot to ProcessDefinition
│   └── ActivityInstance.cs               # Refactor to own ActivityInstanceState
│
├── Fleans.Infrastructure/
│   └── Storage/
│       ├── ProcessDefinitionGrainStorage.cs
│       ├── WorkflowInstanceGrainStorage.cs
│       └── ActivityInstanceGrainStorage.cs
```

---

## Implementation Order

### Task 1: Add persistence interfaces
Create `IAggregateRoot`, `IUnitOfWork`, `ICommandRepository<T>` in `Fleans.Domain/Persistence/`.

### Task 2: Add IAggregateRoot to existing classes
Add marker to `ProcessDefinition` and `WorkflowInstanceState`.

### Task 3: Extract ActivityInstanceState
Create POCO, refactor `ActivityInstance` grain to delegate to it. Update tests.

### Task 4: Add IGrainStorage providers
Create three providers in `Fleans.Infrastructure/Storage/`.

### Task 5: Wire IPersistentState in grains
Update `WorkflowInstance` and `ActivityInstance` constructors. Update tests.

---

## Not In Scope
- No database implementation (EF Core, SQL, etc.)
- No concrete `ICommandRepository<T>` implementations
- Storage providers compile but aren't registered/wired until DB impl exists
