# Merge WorkflowInstanceState with EF Core Entities

**Date:** 2026-02-14
**Status:** Proposed

## Overview

Eliminate the separate `Fleans.Persistence/Entities/` classes by making domain state classes (`WorkflowInstanceState`, `ActivityInstanceEntry`, `WorkflowVariablesState`, `ConditionSequenceState`) directly EF Core-mappable. This removes the `MapToEntity`/`MapToDomain` translation layer in `EfCoreWorkflowInstanceGrainStorage`, reducing code duplication and simplifying persistence logic.

## Motivation

The current architecture maintains two parallel class hierarchies:

1. **Domain state classes** (`Fleans.Domain/States/`) — used by grains and serialized by Orleans
2. **Entity classes** (`Fleans.Persistence/Entities/`) — used by EF Core for database persistence

This duplication requires bidirectional mapping logic in `EfCoreWorkflowInstanceGrainStorage`, increases maintenance burden, and creates opportunities for mapping bugs. By merging these hierarchies, we can use a single set of classes for both Orleans serialization and EF Core persistence.

## Goals

- Single source of truth for workflow instance state structure
- Eliminate `MapToEntity()` and `MapToDomain()` methods
- Maintain clean domain layer (no EF dependencies)
- Preserve Orleans serialization compatibility
- Simplify storage implementation

## Non-Goals

- Change external grain interfaces or behavior
- Optimize query performance (list vs dictionary lookups)
- Add new persistence features

## Section 1: WorkflowInstanceState Root Entity

### Current Structure

```csharp
public class WorkflowInstanceState
{
    public List<ActivityInstanceEntry> ActiveActivities { get; set; }
    public List<ActivityInstanceEntry> CompletedActivities { get; set; }
    public Dictionary<Guid, WorkflowVariablesState> VariableStates { get; set; }
    public Dictionary<Guid, ConditionSequenceState[]> ConditionSequenceStates { get; set; }
}
```

### Proposed Structure

Replace the active/completed split with a single list plus a completion flag. Replace dictionaries with lists where items carry their own IDs.

```csharp
[GenerateSerializer]
public class WorkflowInstanceState
{
    [Id(0)] public Guid Id { get; set; }
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public List<ActivityInstanceEntry> Entries { get; set; }
    [Id(3)] public List<WorkflowVariablesState> VariableStates { get; set; }
    [Id(4)] public List<ConditionSequenceState> ConditionSequenceStates { get; set; }

    // Query methods become LINQ filters
    public IEnumerable<ActivityInstanceEntry> GetActiveActivities()
        => Entries.Where(e => !e.IsCompleted);

    public IEnumerable<ActivityInstanceEntry> GetCompletedActivities()
        => Entries.Where(e => e.IsCompleted);

    public WorkflowVariablesState? GetVariableState(Guid id)
        => VariableStates.FirstOrDefault(v => v.Id == id);

    public ConditionSequenceState[] GetConditionSequenceStates(Guid gatewayActivityInstanceId)
        => ConditionSequenceStates.Where(c => c.GatewayActivityInstanceId == gatewayActivityInstanceId).ToArray();
}
```

### Property Changes

| Property | Type (Before) | Type (After) | Purpose |
|----------|---------------|--------------|---------|
| Id | N/A | `Guid` | EF primary key, set from grain ID |
| ETag | N/A | `string?` | Concurrency token |
| ActiveActivities | `List<ActivityInstanceEntry>` | Removed | Replaced by `Entries` filter |
| CompletedActivities | `List<ActivityInstanceEntry>` | Removed | Replaced by `Entries` filter |
| Entries | N/A | `List<ActivityInstanceEntry>` | Single list with completion flag |
| VariableStates | `Dictionary<Guid, WorkflowVariablesState>` | `List<WorkflowVariablesState>` | EF navigation property |
| ConditionSequenceStates | `Dictionary<Guid, ConditionSequenceState[]>` | `List<ConditionSequenceState>` | EF navigation property |

### Migration Impact

Existing methods that reference `ActiveActivities` and `CompletedActivities` will call `GetActiveActivities()` and `GetCompletedActivities()` instead. Dictionary lookups (`VariableStates[guid]`) become method calls (`GetVariableState(guid)`).

## Section 2: Child State Classes

### ActivityInstanceEntry

**Current:**
```csharp
public record ActivityInstanceEntry(Guid ActivityInstanceId, string ActivityId);
```

**Proposed:**
```csharp
[GenerateSerializer]
public class ActivityInstanceEntry
{
    [Id(0)] public Guid ActivityInstanceId { get; set; }
    [Id(1)] public string ActivityId { get; set; }
    [Id(2)] public Guid WorkflowInstanceId { get; set; }
    [Id(3)] public bool IsCompleted { get; set; }
}
```

| Property | New? | Purpose |
|----------|------|---------|
| ActivityInstanceId | No | Primary key |
| ActivityId | No | Reference to workflow definition activity |
| WorkflowInstanceId | Yes | Foreign key to `WorkflowInstanceState` |
| IsCompleted | Yes | Replaces active/completed list split |

Change from record to class for EF change tracking.

### WorkflowVariablesState

**Current:**
```csharp
[GenerateSerializer]
public class WorkflowVariablesState
{
    [Id(0)] public ExpandoObject Variables { get; set; }
    public void Merge(ExpandoObject variables) { ... }
}
```

**Proposed:**
```csharp
[GenerateSerializer]
public class WorkflowVariablesState
{
    [Id(0)] public Guid Id { get; set; }
    [Id(1)] public Guid WorkflowInstanceId { get; set; }
    [Id(2)] public ExpandoObject Variables { get; set; }

    public void Merge(ExpandoObject variables) { ... }
}
```

| Property | New? | Purpose |
|----------|------|---------|
| Id | Yes | Primary key (previously dictionary key) |
| WorkflowInstanceId | Yes | Foreign key to `WorkflowInstanceState` |
| Variables | No | JSON column for dynamic data |
| Merge() | No | Domain logic unchanged |

### ConditionSequenceState

**Current:**
```csharp
[GenerateSerializer]
public class ConditionSequenceState
{
    [Id(0)] public string ConditionalSequenceFlowId { get; set; }
    [Id(1)] public bool Result { get; set; }
    [Id(2)] public bool IsEvaluated { get; set; }
}
```

**Proposed:**
```csharp
[GenerateSerializer]
public class ConditionSequenceState
{
    [Id(0)] public Guid GatewayActivityInstanceId { get; set; }
    [Id(1)] public string ConditionalSequenceFlowId { get; set; }
    [Id(2)] public bool Result { get; set; }
    [Id(3)] public bool IsEvaluated { get; set; }
    [Id(4)] public Guid WorkflowInstanceId { get; set; }
}
```

| Property | New? | Purpose |
|----------|------|---------|
| GatewayActivityInstanceId | Yes | Composite PK part (previously dictionary key) |
| ConditionalSequenceFlowId | No | Composite PK part |
| Result | No | Condition evaluation result |
| IsEvaluated | No | Evaluation status |
| WorkflowInstanceId | Yes | Foreign key to `WorkflowInstanceState` |

### Key Design Decisions

1. **No EF dependency in domain** — FK/PK properties are plain `Guid`/`string`. Relationships configured via Fluent API in `OnModelCreating`.
2. **Orleans attributes preserved** — `[GenerateSerializer]` and `[Id(N)]` remain on all classes (EF ignores them).
3. **Change tracking** — `ActivityInstanceEntry` changes from record to class for EF tracking.

## Section 3: Storage Layer Changes

### Deleted Files

- `Fleans.Persistence/Entities/WorkflowInstanceEntity.cs`
- `Fleans.Persistence/Entities/ActivityInstanceEntryEntity.cs`
- `Fleans.Persistence/Entities/WorkflowVariablesEntity.cs`
- `Fleans.Persistence/Entities/ConditionSequenceEntity.cs`

### EfCoreWorkflowInstanceGrainStorage Simplification

**Before (pseudocode):**
```csharp
public async Task ReadStateAsync<T>(...)
{
    var entity = await db.WorkflowInstances
        .Include(e => e.Entries)
        .Include(e => e.Variables)
        .Include(e => e.Conditions)
        .FirstOrDefaultAsync(...);

    grainState.State = MapToDomain(entity); // Translation layer
}

public async Task WriteStateAsync<T>(...)
{
    var entity = MapToEntity(grainState.State); // Translation layer
    db.Update(entity);
    await db.SaveChangesAsync();
}
```

**After (pseudocode):**
```csharp
public async Task ReadStateAsync<T>(...)
{
    grainState.State = await db.WorkflowInstanceStates
        .Include(s => s.Entries)
        .Include(s => s.VariableStates)
        .Include(s => s.ConditionSequenceStates)
        .FirstOrDefaultAsync(...);
}

public async Task WriteStateAsync<T>(...)
{
    if (grainState.State.ETag == null)
    {
        db.Add(grainState.State);
    }
    else
    {
        db.Attach(grainState.State);
        SyncChildCollections(grainState.State); // Diff entries/variables/conditions
    }
    await db.SaveChangesAsync();
}
```

**Changes:**
- Delete `MapToEntity()` and `MapToDomain()` methods (200+ lines removed)
- `ReadStateAsync` loads domain state directly with eager loading
- `WriteStateAsync` uses standard EF attach/add pattern
- Child collection diffing logic preserved but operates on domain lists
- `ClearStateAsync` unchanged (just references new type)

### GrainStateDbContext Changes

**Before:**
```csharp
public DbSet<WorkflowInstanceEntity> WorkflowInstances { get; set; }
```

**After:**
```csharp
public DbSet<WorkflowInstanceState> WorkflowInstanceStates { get; set; }
public DbSet<ActivityInstanceEntry> ActivityInstanceEntries { get; set; }
public DbSet<WorkflowVariablesState> WorkflowVariables { get; set; }
public DbSet<ConditionSequenceState> ConditionSequences { get; set; }
```

**OnModelCreating changes:**
- Same table names, same column configurations
- Point entity configurations at domain types instead of entity types
- Configure JSON column for `ExpandoObject` using value converter
- Configure composite key for `ConditionSequenceState`

**Example configuration:**
```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<WorkflowInstanceState>(entity =>
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.ETag).IsConcurrencyToken();
        entity.HasMany(e => e.Entries).WithOne().HasForeignKey(e => e.WorkflowInstanceId);
        entity.HasMany(e => e.VariableStates).WithOne().HasForeignKey(e => e.WorkflowInstanceId);
        entity.HasMany(e => e.ConditionSequenceStates).WithOne().HasForeignKey(e => e.WorkflowInstanceId);
    });

    modelBuilder.Entity<WorkflowVariablesState>(entity =>
    {
        entity.Property(e => e.Variables)
            .HasConversion(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<ExpandoObject>(v));
    });

    modelBuilder.Entity<ConditionSequenceState>(entity =>
    {
        entity.HasKey(e => new { e.GatewayActivityInstanceId, e.ConditionalSequenceFlowId });
    });
}
```

## Section 4: Grain and Caller Impact

### WorkflowInstance Grain Changes

| Current Code | New Code | Reason |
|-------------|----------|--------|
| `State.ActiveActivities.Add(entry)` | `State.Entries.Add(entry)` with `IsCompleted = false` | Single list structure |
| `State.RemoveActiveActivities(...)` | Set `entry.IsCompleted = true` | In-place update |
| `State.AddCompletedActivities(...)` | Delete method | Completion is flag flip |
| `State.GetActiveActivities()` | `State.Entries.Where(e => !e.IsCompleted)` | LINQ filter |
| `State.GetCompletedActivities()` | `State.Entries.Where(e => e.IsCompleted)` | LINQ filter |
| `State.VariableStates[guid]` | `State.GetVariableState(guid)` | List lookup helper |
| `State.ConditionSequenceStates[activityId]` | `State.GetConditionSequenceStates(activityId)` | List filter helper |
| `State.StartWith(entry, variablesId)` | Create `WorkflowVariablesState` with explicit `Id` | Set PK explicitly |

### Example Refactoring

**Before:**
```csharp
var entry = new ActivityInstanceEntry(activityInstanceId, activityId);
State.ActiveActivities.Add(entry);

// Later...
State.RemoveActiveActivities(entry);
State.AddCompletedActivities(entry);
```

**After:**
```csharp
var entry = new ActivityInstanceEntry
{
    ActivityInstanceId = activityInstanceId,
    ActivityId = activityId,
    WorkflowInstanceId = State.Id,
    IsCompleted = false
};
State.Entries.Add(entry);

// Later...
entry.IsCompleted = true;
```

### Interface and Public API

No changes to `IWorkflowInstance` interface. All public methods return same types. Callers see no behavioral difference.

### Performance Considerations

**Dictionary to List trade-off:**
- Dictionary lookup: O(1)
- List lookup: O(n)

**Impact analysis:**
- Typical workflow has 5-20 activities (small n)
- Variable lookups happen once per activity completion (low frequency)
- Condition lookups happen once per gateway evaluation (low frequency)
- List scan overhead negligible for these sizes

**Benefit:**
- Simpler EF mapping (navigation properties vs manual dictionary hydration)
- Cleaner domain model (no dictionary serialization concerns)

## Section 5: Testing Impact

### Unit Tests

**WorkflowInstanceStateTests** — updated to use flat structure:
- Test `GetActiveActivities()` filtering
- Test `GetCompletedActivities()` filtering
- Test `GetVariableState()` lookup
- Test `GetConditionSequenceStates()` filtering
- Verify `IsCompleted` flag transitions

### Integration Tests

**Activity tests** (e.g., `ScriptTaskTests`, `TaskActivityTests`) use `IWorkflowInstance` interface. No changes needed beyond recompilation.

### Persistence Tests

**EfCoreWorkflowInstanceGrainStorageTests** — simplified:
- Delete all `MapToEntity`/`MapToDomain` assertions
- Verify direct state persistence
- Test eager loading of child collections
- Test concurrency token handling
- Test child collection add/update/delete diffing

## Implementation Plan

### Phase 1: Domain Changes
1. Update `ActivityInstanceEntry` from record to class, add `WorkflowInstanceId` and `IsCompleted`
2. Update `WorkflowVariablesState` to add `Id` and `WorkflowInstanceId`
3. Update `ConditionSequenceState` to add `GatewayActivityInstanceId` and `WorkflowInstanceId`
4. Update `WorkflowInstanceState` to flatten structure (single `Entries` list, lists instead of dictionaries)
5. Add query helper methods (`GetActiveActivities`, `GetVariableState`, etc.)

### Phase 2: Persistence Changes
1. Delete `Fleans.Persistence/Entities/` classes
2. Update `GrainStateDbContext` to reference domain types
3. Configure EF relationships in `OnModelCreating`
4. Rewrite `EfCoreWorkflowInstanceGrainStorage` to remove mapping layer
5. Update child collection diffing to operate on domain lists

### Phase 3: Grain Updates
1. Update `WorkflowInstance` grain to use new API
2. Replace active/completed list manipulations with flag updates
3. Replace dictionary lookups with helper method calls
4. Update `StartWith` to set `WorkflowVariablesState.Id` explicitly

### Phase 4: Testing
1. Update `WorkflowInstanceStateTests`
2. Update `EfCoreWorkflowInstanceGrainStorageTests`
3. Run full test suite to verify integration tests pass
4. Add migration test to verify existing data compatibility

## Migration Strategy

### Database Migration

EF Core migration will:
1. Add `IsCompleted` column to `ActivityInstanceEntries` table
2. Add `Id` column to `WorkflowVariables` table
3. Add `GatewayActivityInstanceId` column to `ConditionSequences` table
4. Update primary/foreign keys
5. Populate new columns from existing data structure

### In-Memory Orleans State

Grains currently in memory will have state in old format. On next persistence write, EF will serialize new format. Migration handled by:
1. Deploy new version with backward-compatible reads (can read both formats)
2. Wait for grains to rehydrate and save at least once
3. Deploy version that only writes new format

Since current project has no production persistence yet (state is in-memory), this is a non-issue for initial deployment.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Breaking Orleans serialization | Keep all `[GenerateSerializer]` and `[Id(N)]` attributes, test round-trip serialization |
| EF tracking issues | Change `ActivityInstanceEntry` to class, test update scenarios |
| Performance regression | Benchmark typical workflows, verify list scan overhead is negligible |
| Migration data loss | Write migration tests, verify bidirectional compatibility |

## Alternatives Considered

### Alternative 1: Keep Separate Entities

**Pros:**
- No domain changes required
- Clear separation of concerns

**Cons:**
- Maintains mapping layer complexity
- Code duplication
- Mapping bugs possible

**Decision:** Rejected. Mapping layer overhead not justified for this use case.

### Alternative 2: Auto-Mapper

**Pros:**
- Reduces manual mapping code

**Cons:**
- Still maintains two class hierarchies
- Adds dependency
- Magic behavior harder to debug

**Decision:** Rejected. Merging eliminates mapping entirely, better than improving mapping.

### Alternative 3: Keep Dictionaries in State

**Pros:**
- O(1) lookup performance
- No API changes in grain

**Cons:**
- EF Core dictionary mapping requires extra work (shadow properties or JSON column)
- More complex OnModelCreating configuration
- Harder to query via SQL

**Decision:** Rejected. List overhead negligible for workflow sizes, simpler EF mapping more valuable.

## Files Changed Summary

### Deleted (4 files)
- `Fleans.Persistence/Entities/WorkflowInstanceEntity.cs`
- `Fleans.Persistence/Entities/ActivityInstanceEntryEntity.cs`
- `Fleans.Persistence/Entities/WorkflowVariablesEntity.cs`
- `Fleans.Persistence/Entities/ConditionSequenceEntity.cs`

### Modified - Domain (4 files)
- `Fleans.Domain/States/WorkflowInstanceState.cs` — flatten to single `Entries` list, add `Id`/`ETag`, change dictionaries to lists, add query helpers
- `Fleans.Domain/States/ActivityInstanceEntry.cs` — record to class, add `WorkflowInstanceId` and `IsCompleted`
- `Fleans.Domain/States/WorkflowVariablesState.cs` — add `Id` and `WorkflowInstanceId`
- `Fleans.Domain/States/ConditionSequenceState.cs` — add `GatewayActivityInstanceId` and `WorkflowInstanceId`

### Modified - Grain (1 file)
- `Fleans.Domain/WorkflowInstance.cs` — adapt to flat list API, use query helpers

### Modified - Persistence (2 files)
- `Fleans.Persistence/GrainStateDbContext.cs` — point `DbSet`s at domain types, configure relationships
- `Fleans.Persistence/EfCoreWorkflowInstanceGrainStorage.cs` — remove mapping methods, simplify CRUD, direct state persistence

### Modified - Tests (2+ files)
- `Fleans.Domain.Tests/WorkflowInstanceStateTests.cs` — test flat structure and query helpers
- `Fleans.Persistence.Tests/EfCoreWorkflowInstanceGrainStorageTests.cs` — remove mapping assertions, test direct persistence

## Success Criteria

1. All existing tests pass after refactoring
2. `MapToEntity` and `MapToDomain` methods deleted
3. No EF dependencies in `Fleans.Domain` project
4. `EfCoreWorkflowInstanceGrainStorage` code reduced by at least 30%
5. Round-trip Orleans serialization works
6. EF Core migrations generate successfully

## References

- EF Core persistence design: `docs/plans/2026-02-13-efcore-workflow-instance-storage.md`
- Orleans grain storage: https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-persistence/
- EF Core value conversions: https://learn.microsoft.com/en-us/ef/core/modeling/value-conversions
