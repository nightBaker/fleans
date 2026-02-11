# State-Based CQRS Persistence for Fleans

## Context

All grain state is currently in-memory and lost on silo restart. This plan separates **workflow definitions** (CRUD data) from **workflow runtime state** (grain-level execution state), persisting each with the appropriate technology.

## Architecture

- **Workflow definitions** → EF Core, domain models mapped directly as normalized entities (TPH for polymorphism, shadow FKs for relationships)
- **Workflow runtime state** → Orleans `IPersistentState<T>` with Redis
- **CQRS separation** → `ICommandRepository<T>` for writes, `IQueryRepository<T>` for reads
- **Provider abstraction** → `Fleans.Persistence` + `Fleans.Persistence.PostgreSql`

---

## PR 1: EF Core for Workflow Definitions

### Step 1: Domain interfaces (`Fleans.Domain`)

**New files:**
- `IAggregateRoot.cs` — marker interface
- `Repositories/IUnitOfWork.cs`
- `Repositories/ICommandRepository.cs`
- `Repositories/IQueryRepository.cs`

```csharp
public interface ICommandRepository<T> where T : IAggregateRoot
{
    IUnitOfWork UnitOfWork { get; }
    Task<T> GetAsync(Expression<Func<T, bool>> predicate);
    void Add(T item);
    T Remove(T item);
    Task<List<T>> GetListAsync(Expression<Func<T, bool>> predicate);
}

public interface IQueryRepository<T> where T : IAggregateRoot
{
    Task<T?> GetAsync(Expression<Func<T, bool>> predicate);
    Task<List<T>> GetListAsync(Expression<Func<T, bool>> predicate);
    Task<int> CountAsync(Expression<Func<T, bool>> predicate);
}
```

Mark `WorkflowDefinition` with `IAggregateRoot`. It is the **only aggregate root** — `ProcessDefinition`, `Activity`, and `SequenceFlow` are regular entities (not aggregate roots). When loading a `WorkflowDefinition` from the DB, the repository always includes all its children (Activities + SequenceFlows).

`ProcessDefinition` is persisted as a separate entity table that references `WorkflowDefinition` via `ProcessDefinitionId`. It is queried directly via DbContext for metadata operations (list definitions, get BPMN XML) but is NOT accessed through the generic repository.

### Step 2: EF Core normalized entity mapping

**Schema** (4 tables):

```
ProcessDefinitions
├── ProcessDefinitionId (PK, string)
├── ProcessDefinitionKey (indexed)
├── Version
├── DeployedAt
├── BpmnXml
└── WorkflowId (from owned WorkflowDefinition)

Activities (TPH — single table, discriminator column)
├── ActivityId (PK part 1, string)
├── ProcessDefinitionId (PK part 2, shadow FK → ProcessDefinitions)
├── Discriminator (string: StartEvent, EndEvent, TaskActivity, ScriptTask, ExclusiveGateway, ParallelGateway)
├── Script (nullable, ScriptTask only)
├── ScriptFormat (nullable, ScriptTask only)
└── IsFork (nullable, ParallelGateway only)

SequenceFlows (TPH — single table, discriminator column)
├── SequenceFlowId (PK part 1, string)
├── ProcessDefinitionId (PK part 2, shadow FK → ProcessDefinitions)
├── SourceActivityId (shadow FK → Activities)
├── TargetActivityId (shadow FK → Activities)
├── Discriminator (string: SequenceFlow, ConditionalSequenceFlow, DefaultSequenceFlow)
└── Condition (nullable, ConditionalSequenceFlow only)
```

**Challenge: domain records use positional constructors and have no FK properties.**

EF Core handles this via:
- **Shadow properties** for all FK columns (`ProcessDefinitionId` on Activity/SequenceFlow, `SourceActivityId`/`TargetActivityId` on SequenceFlow)
- **TPH discriminator** for Activity and SequenceFlow hierarchies
- **Constructor binding** — EF Core binds positional record params by name

**Key EF Core configuration** in `OnModelCreating`:

```csharp
// ProcessDefinition — aggregate root
modelBuilder.Entity<ProcessDefinition>(entity =>
{
    entity.HasKey(e => e.ProcessDefinitionId);
    entity.HasIndex(e => e.ProcessDefinitionKey);
    entity.HasIndex(e => new { e.ProcessDefinitionKey, e.Version }).IsUnique();
    entity.Ignore(e => e.Workflow); // Workflow is reconstructed from Activities + SequenceFlows
});

// Activity — TPH, composite key (ProcessDefinitionId, ActivityId)
modelBuilder.Entity<Activity>(entity =>
{
    entity.Property<string>("ProcessDefinitionId");
    entity.HasKey("ProcessDefinitionId", nameof(Activity.ActivityId));
    entity.HasDiscriminator<string>("Discriminator")
        .HasValue<StartEvent>("StartEvent")
        .HasValue<EndEvent>("EndEvent")
        .HasValue<TaskActivity>("TaskActivity")
        .HasValue<ScriptTask>("ScriptTask")
        .HasValue<ExclusiveGateway>("ExclusiveGateway")
        .HasValue<ParallelGateway>("ParallelGateway");
});

// SequenceFlow — TPH, composite key (ProcessDefinitionId, SequenceFlowId)
modelBuilder.Entity<SequenceFlow>(entity =>
{
    entity.Property<string>("ProcessDefinitionId");
    entity.Property<string>("SourceActivityId");
    entity.Property<string>("TargetActivityId");
    entity.HasKey("ProcessDefinitionId", nameof(SequenceFlow.SequenceFlowId));

    entity.HasOne(sf => sf.Source)
        .WithMany()
        .HasForeignKey("ProcessDefinitionId", "SourceActivityId")
        .OnDelete(DeleteBehavior.NoAction);

    entity.HasOne(sf => sf.Target)
        .WithMany()
        .HasForeignKey("ProcessDefinitionId", "TargetActivityId")
        .OnDelete(DeleteBehavior.NoAction);

    entity.HasDiscriminator<string>("Discriminator")
        .HasValue<SequenceFlow>("SequenceFlow")
        .HasValue<ConditionalSequenceFlow>("ConditionalSequenceFlow")
        .HasValue<DefaultSequenceFlow>("DefaultSequenceFlow");
});
```

**Important design choice: `Ignore(e => e.Workflow)`**

`ProcessDefinition.Workflow` is a `WorkflowDefinition` containing `List<Activity>` and `List<SequenceFlow>`. Since Activities and SequenceFlows are now separate tables, we **ignore** the Workflow navigation property on the EF side. On read, the repository reconstructs the `WorkflowDefinition` from the Activity and SequenceFlow tables.

The generic `EfCommandRepository<WorkflowDefinition>` needs a **specialized** implementation that includes child entities. `WorkflowDefinitionCommandRepository` extends the generic repo:

```csharp
public class WorkflowDefinitionCommandRepository : EfCommandRepository<WorkflowDefinition>
{
    public override async Task<WorkflowDefinition> GetAsync(Expression<Func<WorkflowDefinition, bool>> predicate)
    {
        var workflow = await Context.Set<WorkflowDefinition>()
            .Include(w => w.Activities)
            .Include(w => w.SequenceFlows)
                .ThenInclude(sf => sf.Source)
            .Include(w => w.SequenceFlows)
                .ThenInclude(sf => sf.Target)
            .FirstAsync(predicate);

        return workflow;
    }

    public override void Add(WorkflowDefinition item)
    {
        Context.Set<WorkflowDefinition>().Add(item);
        // EF Core cascade-adds children via navigation properties
        // Shadow FKs populated via OnModelCreating config
    }
}
```

`ProcessDefinition` is queried directly via DbContext for metadata operations (not through the generic repo).

### Step 3: Create `Fleans.Persistence.PostgreSql` project

Extension method registers the specific repos:
```csharp
services.AddDbContext<FleansPersistenceDbContext>(options => options.UseNpgsql(connectionString));
services.AddScoped<ICommandRepository<WorkflowDefinition>, WorkflowDefinitionCommandRepository>();
services.AddScoped<IQueryRepository<WorkflowDefinition>, WorkflowDefinitionQueryRepository>();
```

### Step 4: Refactor `WorkflowInstanceFactoryGrain`

- Inject `ICommandRepository<WorkflowDefinition>` for deploy (saves aggregate with all children)
- For `ProcessDefinition` metadata: access DbContext directly via scoped service
- **Scoped service in singleton grain problem**: inject `IServiceProvider`, create scope per operation:
  ```csharp
  using var scope = _serviceProvider.CreateScope();
  var repo = scope.ServiceProvider.GetRequiredService<ICommandRepository<WorkflowDefinition>>();
  var dbContext = scope.ServiceProvider.GetRequiredService<FleansPersistenceDbContext>();
  ```
- Instance tracking stays in `IPersistentState<WorkflowFactoryGrainState>`

### Step 5: Update `WorkflowEngine`

- Inject `IQueryRepository<WorkflowDefinition>` for loading full workflow aggregates (with Activities + SequenceFlows)
- For `ProcessDefinition` metadata queries (list, BPMN XML): access DbContext directly
- `WorkflowEngine` is a singleton — same scoping pattern with `IServiceProvider.CreateScope()` or `IDbContextFactory`

### Step 6: Wire up Aspire + migrations

- Add PostgreSQL resource in Aspire
- Generate initial EF migration
- Run migrations on startup

### Files (PR 1):
| File | Change |
|------|--------|
| `Fleans.Domain/IAggregateRoot.cs` | **New** |
| `Fleans.Domain/Repositories/IUnitOfWork.cs` | **New** |
| `Fleans.Domain/Repositories/ICommandRepository.cs` | **New** |
| `Fleans.Domain/Repositories/IQueryRepository.cs` | **New** |
| `Fleans.Domain/Workflow.cs` | Add `IAggregateRoot` to `WorkflowDefinition` |
| `Fleans.Persistence/` | **New project** — DbContext (normalized mapping), EfCommandRepository, EfQueryRepository, WorkflowDefinitionCommandRepository |
| `Fleans.Persistence.PostgreSql/` | **New project** — provider config |
| `Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs` | Use `ICommandRepository<WorkflowDefinition>` via scoped service |
| `Fleans.Application/WorkflowFactory/WorkflowFactoryGrainState.cs` | **New** — instance tracking only |
| `Fleans.Application/WorkflowEngine.cs` | Use `IQueryRepository<WorkflowDefinition>` for reads |
| `Fleans.Api/Program.cs` | Register PostgreSQL persistence |
| `Fleans.Aspire/Program.cs` | Add PostgreSQL resource |
| `Fleans.sln` | Add new projects |

---

## PR 2: Orleans `IPersistentState<T>` for Runtime State

(Unchanged from previous plan version — see grain state classes, wiring, test updates)

### Grain state classes in `Fleans.Domain/Persistence/`:
- `StorageConstants.cs`
- `WorkflowInstanceGrainState.cs`
- `WorkflowInstanceStateGrainState.cs` (GUIDs instead of grain refs)
- `ActivityInstanceGrainState.cs`

### Wire `IPersistentState<T>` into:
- `ActivityInstance.cs` (simplest)
- `WorkflowInstanceState.cs` (GUID conversion, batch `TransitionActivities`)
- `WorkflowInstance.cs`
- `WorkflowInstanceFactoryGrain.cs` (instance tracking)

### Register Redis `"WorkflowStore"` provider, update test `SiloConfigurator`

---

## PR 3: CQRS Events and Read-Side Projections

### Events:
- `WorkflowInstanceCreatedEvent`, `WorkflowInstanceStartedEvent`, `WorkflowInstanceCompletedEvent`, `ActivityInstanceCompletedEvent`

### Read models:
- `WorkflowInstanceReadModel`, `IWorkflowInstanceReadRepository`, in-memory implementation

---

## Risks and mitigations

1. **EF Core + positional records**: Records like `Activity(string ActivityId)` need EF Core constructor binding. If binding fails, add private parameterless constructors.
2. **Shadow FK population**: When adding entities, shadow FKs must be set explicitly via `Entry().Property()`. Forgetting this causes FK constraint violations.
3. **Scoped DbContext in singleton grain/service**: Use `IServiceProvider.CreateScope()` or `IDbContextFactory` to avoid stale/disposed DbContext.
4. **SequenceFlow circular refs**: `Source`/`Target` navigation properties point back to Activities. EF Core handles this with `NoAction` delete behavior, but loading must use explicit `.Include()`.
5. **Test mocking for EF**: Tests need either an in-memory EF provider (`UseInMemoryDatabase`) or mock `ICommandRepository`/`IQueryRepository` — configure in `TestSiloConfigurator`.

## Verification

1. `dotnet build` — all projects compile
2. `dotnet test` — all existing tests pass
3. Run via Aspire — deploy BPMN, start instance, verify completion
4. Check PostgreSQL tables contain normalized ProcessDefinition, Activity, and SequenceFlow rows
5. Restart silo — definitions survive (PostgreSQL), runtime state survives (Redis)
