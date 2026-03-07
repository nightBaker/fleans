# Signal Start Event Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable broadcast-triggered workflow instantiation via BPMN signal start events, plus migrate start event listener persistence from JSON columns to relational join tables.

**Architecture:** New `SignalStartEvent` domain activity + `SignalStartEventListenerGrain` (mirrors message start event pattern). Persistence layer migrates `MessageStartEventListenerState` from JSON `List<string>` to a `MessageStartEventRegistrations` join table, and creates equivalent `SignalStartEventRegistrations` table. API `SendSignal` becomes fan-out: broadcast to running instances AND create new instances simultaneously.

**Tech Stack:** .NET 10, Orleans 9.2.1, EF Core, MSTest, Orleans.TestingHost

**Design doc:** `docs/plans/2026-03-07-signal-start-event-design.md`

---

### Task 1: Domain — `SignalStartEvent` Activity

**Files:**
- Create: `src/Fleans/Fleans.Domain/Activities/SignalStartEvent.cs`
- Test: `src/Fleans/Fleans.Domain.Tests/SignalStartEventDomainTests.cs`

**Step 1: Create `SignalStartEvent.cs`**

Copy `MessageStartEvent.cs` exactly, swap `MessageDefinitionId` → `SignalDefinitionId`:

```csharp
using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record SignalStartEvent(
    string ActivityId,
    [property: Id(1)] string SignalDefinitionId) : Activity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        await activityContext.Complete();
        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return Task.FromResult(nextFlow != null ? new List<ActivityTransition> { new(nextFlow.Target) } : new List<ActivityTransition>());
    }
}
```

**Step 2: Write domain tests**

Create `SignalStartEventDomainTests.cs` — copy `MessageStartEventDomainTests.cs` pattern, replace `MessageStartEvent`/`MessageDefinitionId` with `SignalStartEvent`/`SignalDefinitionId`:

Two tests:
- `ExecuteAsync_ShouldCompleteImmediately` — verify `activityContext.Complete()` called, event published
- `GetNextActivities_ShouldReturnTarget_ViaSequenceFlow` — verify outgoing flow resolved

**Step 3: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~SignalStartEventDomainTests" --nologo -v q
```
Expected: 2 PASS

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/SignalStartEvent.cs src/Fleans/Fleans.Domain.Tests/SignalStartEventDomainTests.cs
git commit -m "feat: add SignalStartEvent domain activity with tests"
```

---

### Task 2: Domain — `SignalStartEventListenerState`

**Files:**
- Create: `src/Fleans/Fleans.Domain/States/SignalStartEventListenerState.cs`
- Modify: `src/Fleans/Fleans.Domain/GrainStorageNames.cs`

**Step 1: Create `SignalStartEventListenerState.cs`**

Copy `MessageStartEventListenerState.cs` exactly:

```csharp
namespace Fleans.Domain.States;

[GenerateSerializer]
public class SignalStartEventListenerState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public List<string> ProcessDefinitionKeys { get; set; } = [];

    public void AddProcess(string processDefinitionKey)
    {
        if (!ProcessDefinitionKeys.Contains(processDefinitionKey))
            ProcessDefinitionKeys.Add(processDefinitionKey);
    }

    public void RemoveProcess(string processDefinitionKey)
    {
        ProcessDefinitionKeys.Remove(processDefinitionKey);
    }

    public bool IsEmpty => ProcessDefinitionKeys.Count == 0;
}
```

**Step 2: Add storage name constant**

In `GrainStorageNames.cs`, add:

```csharp
public const string SignalStartEventListeners = "signalStartEventListeners";
```

**Step 3: Build**

```bash
dotnet build --nologo -v q
```
Expected: 0 errors

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/SignalStartEventListenerState.cs src/Fleans/Fleans.Domain/GrainStorageNames.cs
git commit -m "feat: add SignalStartEventListenerState and storage name"
```

---

### Task 3: Persistence — Migrate `MessageStartEventListenerState` to Join Table

**Files:**
- Create: `src/Fleans/Fleans.Domain/States/StartEventRegistration.cs`
- Modify: `src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs` — replace JSON converter with join table
- Modify: `src/Fleans/Fleans.Persistence/FleanCommandDbContext.cs` — add DbSets
- Modify: `src/Fleans/Fleans.Persistence/EfCoreMessageStartEventListenerGrainStorage.cs` — diff rows instead of JSON

This task introduces a shared `StartEventRegistration` record used by both message and signal listener tables.

**Step 1: Create `StartEventRegistration.cs`**

```csharp
namespace Fleans.Domain.States;

[GenerateSerializer]
public record StartEventRegistration(
    [property: Id(0)] string EventName,
    [property: Id(1)] string ProcessDefinitionKey);
```

`EventName` is the FK back to the parent listener table (message name or signal name).

**Step 2: Update `FleanModelConfiguration.cs`**

Replace the `MessageStartEventListenerState` entity config (currently lines 174-184) — remove the JSON converter, add `HasMany` navigation to `StartEventRegistration`:

Replace:
```csharp
modelBuilder.Entity<MessageStartEventListenerState>(entity =>
{
    entity.ToTable("MessageStartEventListeners");
    entity.HasKey(e => e.Key);
    entity.Property(e => e.Key).HasMaxLength(512);
    entity.Property(e => e.ETag).HasMaxLength(64);
    entity.Property(e => e.ProcessDefinitionKeys)
        .HasConversion(
            v => JsonConvert.SerializeObject(v),
            v => JsonConvert.DeserializeObject<List<string>>(v) ?? new List<string>());
});
```

With:
```csharp
modelBuilder.Entity<MessageStartEventListenerState>(entity =>
{
    entity.ToTable("MessageStartEventListeners");
    entity.HasKey(e => e.Key);
    entity.Property(e => e.Key).HasMaxLength(512);
    entity.Property(e => e.ETag).HasMaxLength(64);
    entity.Ignore(e => e.ProcessDefinitionKeys);
});

modelBuilder.Entity<StartEventRegistration>(entity =>
{
    entity.ToTable("StartEventRegistrations");
    entity.HasKey(e => new { e.EventName, e.ProcessDefinitionKey });
    entity.Property(e => e.EventName).HasMaxLength(512);
    entity.Property(e => e.ProcessDefinitionKey).HasMaxLength(256);
});
```

Note: Both message and signal listeners share the same `StartEventRegistrations` table. The `EventName` column serves as the natural key linking to either listener type.

**Step 3: Add DbSet to `FleanCommandDbContext.cs`**

```csharp
public DbSet<StartEventRegistration> StartEventRegistrations => Set<StartEventRegistration>();
```

**Step 4: Rewrite `EfCoreMessageStartEventListenerGrainStorage.cs`**

Replace the current implementation with row-diffing logic:

```csharp
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreMessageStartEventListenerGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreMessageStartEventListenerGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();

        var state = await db.MessageStartEventListeners.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == id);

        if (state is not null)
        {
            var registrations = await db.StartEventRegistrations.AsNoTracking()
                .Where(r => r.EventName == id)
                .ToListAsync();

            state.ProcessDefinitionKeys = registrations.Select(r => r.ProcessDefinitionKey).ToList();

            grainState.State = (T)(object)state;
            grainState.ETag = state.ETag;
            grainState.RecordExists = true;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = (MessageStartEventListenerState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.MessageStartEventListeners.FindAsync(id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.MessageStartEventListeners.Add(state);
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            existing.ETag = newETag;
        }

        // Diff registrations
        var existingRegs = await db.StartEventRegistrations
            .Where(r => r.EventName == id)
            .ToListAsync();

        var existingKeys = existingRegs.Select(r => r.ProcessDefinitionKey).ToHashSet();
        var newKeys = state.ProcessDefinitionKeys.ToHashSet();

        foreach (var reg in existingRegs.Where(r => !newKeys.Contains(r.ProcessDefinitionKey)))
            db.StartEventRegistrations.Remove(reg);

        foreach (var key in newKeys.Where(k => !existingKeys.Contains(k)))
            db.StartEventRegistrations.Add(new StartEventRegistration(id, key));

        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var existing = await db.MessageStartEventListeners.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            // Cascade will delete registrations
            db.MessageStartEventListeners.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
```

**Step 5: Run existing persistence tests**

```bash
dotnet test --filter "FullyQualifiedName~EfCoreMessageStartEventListenerGrainStorage" --nologo -v q
```

If no tests exist yet for this storage, run all persistence tests:
```bash
dotnet test --project src/Fleans/Fleans.Persistence.Tests --nologo -v q
```
Expected: All pass

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/StartEventRegistration.cs \
  src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs \
  src/Fleans/Fleans.Persistence/FleanCommandDbContext.cs \
  src/Fleans/Fleans.Persistence/EfCoreMessageStartEventListenerGrainStorage.cs
git commit -m "refactor: migrate MessageStartEventListener persistence to join table"
```

---

### Task 4: Persistence — `SignalStartEventListenerGrainStorage`

**Files:**
- Create: `src/Fleans/Fleans.Persistence/EfCoreSignalStartEventListenerGrainStorage.cs`
- Modify: `src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs` — add `SignalStartEventListenerState` entity
- Modify: `src/Fleans/Fleans.Persistence/FleanCommandDbContext.cs` — add DbSet
- Modify: `src/Fleans/Fleans.Persistence/DependencyInjection.cs` — register storage

**Step 1: Add entity config to `FleanModelConfiguration.cs`**

After the `MessageStartEventListenerState` config, add:

```csharp
modelBuilder.Entity<SignalStartEventListenerState>(entity =>
{
    entity.ToTable("SignalStartEventListeners");
    entity.HasKey(e => e.Key);
    entity.Property(e => e.Key).HasMaxLength(512);
    entity.Property(e => e.ETag).HasMaxLength(64);
    entity.Ignore(e => e.ProcessDefinitionKeys);
});
```

No separate `StartEventRegistration` config needed — it's already configured in Task 3 and shared.

**Step 2: Add DbSet to `FleanCommandDbContext.cs`**

```csharp
public DbSet<SignalStartEventListenerState> SignalStartEventListeners => Set<SignalStartEventListenerState>();
```

**Step 3: Create `EfCoreSignalStartEventListenerGrainStorage.cs`**

Copy `EfCoreMessageStartEventListenerGrainStorage.cs` from Task 3, replace all `MessageStartEventListenerState` → `SignalStartEventListenerState` and `MessageStartEventListeners` → `SignalStartEventListeners`:

```csharp
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreSignalStartEventListenerGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreSignalStartEventListenerGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();

        var state = await db.SignalStartEventListeners.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == id);

        if (state is not null)
        {
            var registrations = await db.StartEventRegistrations.AsNoTracking()
                .Where(r => r.EventName == id)
                .ToListAsync();

            state.ProcessDefinitionKeys = registrations.Select(r => r.ProcessDefinitionKey).ToList();

            grainState.State = (T)(object)state;
            grainState.ETag = state.ETag;
            grainState.RecordExists = true;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = (SignalStartEventListenerState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.SignalStartEventListeners.FindAsync(id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.SignalStartEventListeners.Add(state);
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            existing.ETag = newETag;
        }

        // Diff registrations
        var existingRegs = await db.StartEventRegistrations
            .Where(r => r.EventName == id)
            .ToListAsync();

        var existingKeys = existingRegs.Select(r => r.ProcessDefinitionKey).ToHashSet();
        var newKeys = state.ProcessDefinitionKeys.ToHashSet();

        foreach (var reg in existingRegs.Where(r => !newKeys.Contains(r.ProcessDefinitionKey)))
            db.StartEventRegistrations.Remove(reg);

        foreach (var key in newKeys.Where(k => !existingKeys.Contains(k)))
            db.StartEventRegistrations.Add(new StartEventRegistration(id, key));

        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var existing = await db.SignalStartEventListeners.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.SignalStartEventListeners.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
```

**Step 4: Register in `DependencyInjection.cs`**

Add after the `MessageStartEventListeners` registration:

```csharp
services.AddKeyedSingleton<IGrainStorage, EfCoreSignalStartEventListenerGrainStorage>(GrainStorageNames.SignalStartEventListeners);
```

**Step 5: Build**

```bash
dotnet build --nologo -v q
```
Expected: 0 errors

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Persistence/EfCoreSignalStartEventListenerGrainStorage.cs \
  src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs \
  src/Fleans/Fleans.Persistence/FleanCommandDbContext.cs \
  src/Fleans/Fleans.Persistence/DependencyInjection.cs
git commit -m "feat: add SignalStartEventListener persistence with relational join table"
```

---

### Task 5: Grain Interface and Implementation

**Files:**
- Create: `src/Fleans/Fleans.Application/Grains/ISignalStartEventListenerGrain.cs`
- Create: `src/Fleans/Fleans.Application/Grains/SignalStartEventListenerGrain.cs`

**Step 1: Create grain interface**

```csharp
namespace Fleans.Application.Grains;

public interface ISignalStartEventListenerGrain : IGrainWithStringKey
{
    ValueTask RegisterProcess(string processDefinitionKey);
    ValueTask UnregisterProcess(string processDefinitionKey);
    ValueTask<List<Guid>> FireSignalStartEvent();
}
```

No `ExpandoObject variables` parameter — signals carry no payload.

**Step 2: Create grain implementation**

Copy `MessageStartEventListenerGrain.cs`, adapt:
- Replace `MessageStartEvent` → `SignalStartEvent`, `MessageDefinitionId` → `SignalDefinitionId`
- Replace `definition.Messages` → `definition.Signals`
- Replace `FireMessageStartEvent(ExpandoObject variables)` → `FireSignalStartEvent()` (no variables param)
- Remove `SetInitialVariables(variables)` call — signals carry no payload
- Use `PersistentState` with `GrainStorageNames.SignalStartEventListeners`
- EventId range: 9200+ (to avoid collision with message listener 9100+)

```csharp
using Fleans.Application.WorkflowFactory;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class SignalStartEventListenerGrain : Grain, ISignalStartEventListenerGrain
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<SignalStartEventListenerGrain> _logger;
    private readonly IPersistentState<SignalStartEventListenerState> _state;

    private SignalStartEventListenerState State => _state.State;

    public SignalStartEventListenerGrain(
        [PersistentState("state", GrainStorageNames.SignalStartEventListeners)] IPersistentState<SignalStartEventListenerState> state,
        IGrainFactory grainFactory,
        ILogger<SignalStartEventListenerGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async ValueTask RegisterProcess(string processDefinitionKey)
    {
        State.AddProcess(processDefinitionKey);
        await _state.WriteStateAsync();
        LogProcessRegistered(this.GetPrimaryKeyString(), processDefinitionKey);
    }

    public async ValueTask UnregisterProcess(string processDefinitionKey)
    {
        State.RemoveProcess(processDefinitionKey);
        await _state.WriteStateAsync();

        if (State.IsEmpty)
            await _state.ClearStateAsync();

        LogProcessUnregistered(this.GetPrimaryKeyString(), processDefinitionKey);
    }

    public async ValueTask<List<Guid>> FireSignalStartEvent()
    {
        var signalName = this.GetPrimaryKeyString();
        var createdIds = new List<Guid>();

        if (State.ProcessDefinitionKeys.Count == 0)
        {
            LogNoRegisteredProcesses(signalName);
            return createdIds;
        }

        var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

        foreach (var processDefinitionKey in State.ProcessDefinitionKeys)
        {
            try
            {
                var instanceId = Guid.NewGuid();
                var instance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);

                var definition = await factory.GetLatestWorkflowDefinition(processDefinitionKey);

                var signalStartActivityId = FindSignalStartActivityId(definition, signalName);

                await instance.SetWorkflow(definition, signalStartActivityId);
                await instance.StartWorkflow();

                createdIds.Add(instanceId);
                LogSignalStartEventFired(signalName, processDefinitionKey, instanceId);
            }
            catch (Exception ex)
            {
                LogSignalStartEventFailed(signalName, processDefinitionKey, ex);
            }
        }

        return createdIds;
    }

    private static string? FindSignalStartActivityId(IWorkflowDefinition definition, string signalName)
    {
        foreach (var activity in definition.Activities.OfType<SignalStartEvent>())
        {
            var sigDef = definition.Signals.FirstOrDefault(s => s.Id == activity.SignalDefinitionId);
            if (sigDef?.Name == signalName)
                return activity.ActivityId;
        }
        return null;
    }

    [LoggerMessage(EventId = 9200, Level = LogLevel.Information, Message = "Registered process {ProcessDefinitionKey} for signal start event '{SignalName}'")]
    private partial void LogProcessRegistered(string signalName, string processDefinitionKey);

    [LoggerMessage(EventId = 9201, Level = LogLevel.Information, Message = "Unregistered process {ProcessDefinitionKey} from signal start event '{SignalName}'")]
    private partial void LogProcessUnregistered(string signalName, string processDefinitionKey);

    [LoggerMessage(EventId = 9202, Level = LogLevel.Information, Message = "Signal start event fired for '{SignalName}', process {ProcessDefinitionKey}, created instance {InstanceId}")]
    private partial void LogSignalStartEventFired(string signalName, string processDefinitionKey, Guid instanceId);

    [LoggerMessage(EventId = 9203, Level = LogLevel.Warning, Message = "No registered processes for signal start event '{SignalName}'")]
    private partial void LogNoRegisteredProcesses(string signalName);

    [LoggerMessage(EventId = 9204, Level = LogLevel.Error, Message = "Failed to start workflow for signal '{SignalName}', process {ProcessDefinitionKey}")]
    private partial void LogSignalStartEventFailed(string signalName, string processDefinitionKey, Exception ex);
}
```

**Step 3: Build**

```bash
dotnet build --nologo -v q
```
Expected: 0 errors

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/ISignalStartEventListenerGrain.cs \
  src/Fleans/Fleans.Application/Grains/SignalStartEventListenerGrain.cs
git commit -m "feat: add SignalStartEventListenerGrain"
```

---

### Task 6: BPMN Parsing

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs` — add `signalEventDefinition` check in startEvent loop
- Test: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/SignalStartEventTests.cs`

**Step 1: Modify `BpmnConverter.cs`**

In the `<startEvent>` parsing loop, after the `messageEventDefinition` check and before the `else` (plain StartEvent), add:

```csharp
else if (startEvent.Element(Bpmn + "signalEventDefinition") is { } sigDef)
{
    var signalRef = sigDef.Attribute("signalRef")?.Value
        ?? throw new InvalidOperationException(
            $"startEvent '{id}' signalEventDefinition must have a signalRef attribute");
    activity = new SignalStartEvent(id, signalRef);
}
```

**Step 2: Write BPMN converter tests**

Create `SignalStartEventTests.cs` — copy `MessageStartEventTests.cs` pattern, replace message XML with signal XML:

Three tests:
- `ConvertFromXmlAsync_ShouldParseSignalStartEvent` — BPMN with `<signal id="sig1" name="orderSignal"/>` and `<startEvent><signalEventDefinition signalRef="sig1"/></startEvent>`. Assert activity is `SignalStartEvent` with `SignalDefinitionId == "sig1"`.
- `ConvertFromXmlAsync_ShouldThrow_WhenSignalRefMissing` — missing `signalRef` attribute throws `InvalidOperationException`.
- `ConvertFromXmlAsync_ShouldParseSequenceFlow_FromSignalStartEvent` — verify sequence flow source/target correct.

**Step 3: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~SignalStartEventTests" --project src/Fleans/Fleans.Infrastructure.Tests --nologo -v q
```
Expected: 3 PASS

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs \
  src/Fleans/Fleans.Infrastructure.Tests/BpmnConverter/SignalStartEventTests.cs
git commit -m "feat: parse signalEventDefinition on startEvent in BpmnConverter"
```

---

### Task 7: `SetWorkflow` — Add `SignalStartEvent` to Auto-Detect

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs` — line 66

**Step 1: Update the pattern match**

In `WorkflowInstance.cs` line 66, change:

```csharp
startActivity = workflow.Activities.FirstOrDefault(a => a is StartEvent or TimerStartEvent or MessageStartEvent)
```

To:

```csharp
startActivity = workflow.Activities.FirstOrDefault(a => a is StartEvent or TimerStartEvent or MessageStartEvent or SignalStartEvent)
```

Also update the error message on line 67:

```csharp
?? throw new InvalidOperationException("Workflow must have a StartEvent, TimerStartEvent, MessageStartEvent, or SignalStartEvent");
```

**Step 2: Build**

```bash
dotnet build --nologo -v q
```
Expected: 0 errors

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "feat: add SignalStartEvent to SetWorkflow auto-detect"
```

---

### Task 8: Deployment Integration

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs` — `DeployWorkflow` method

**Step 1: Add signal start event registration**

After the existing message start event registration block (lines ~155-181), add an analogous block for signals:

```csharp
// Register/unregister signal start event listeners
var newSignalNames = new HashSet<string>();
foreach (var signalStart in workflowWithId.Activities.OfType<SignalStartEvent>())
{
    var signalDefinition = workflowWithId.Signals.FirstOrDefault(s => s.Id == signalStart.SignalDefinitionId);
    if (signalDefinition != null)
    {
        newSignalNames.Add(signalDefinition.Name);
        var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(signalDefinition.Name);
        await listener.RegisterProcess(processDefinitionKey);
    }
}

// Unregister previous version's signal start events that are no longer present
if (versions.Count > 1)
{
    var previousWorkflow = versions[^2].Workflow;
    foreach (var oldSignalStart in previousWorkflow.Activities.OfType<SignalStartEvent>())
    {
        var oldSignalDef = previousWorkflow.Signals.FirstOrDefault(s => s.Id == oldSignalStart.SignalDefinitionId);
        if (oldSignalDef != null && !newSignalNames.Contains(oldSignalDef.Name))
        {
            var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(oldSignalDef.Name);
            await listener.UnregisterProcess(processDefinitionKey);
        }
    }
}
```

**Step 2: Build**

```bash
dotnet build --nologo -v q
```
Expected: 0 errors

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs
git commit -m "feat: register SignalStartEvent listeners on workflow deployment"
```

---

### Task 9: API — Fan-Out `SendSignal`

**Files:**
- Modify: `src/Fleans/Fleans.Application/IWorkflowCommandService.cs` — add `SendSignal` method + result type
- Modify: `src/Fleans/Fleans.Application/WorkflowCommandService.cs` — implement `SendSignal`
- Modify: `src/Fleans/Fleans.ServiceDefaults/DTOs/SendSignalResponse.cs` — add `WorkflowInstanceIds`
- Modify: `src/Fleans/Fleans.Api/Controllers/WorkflowController.cs` — delegate to command service

**Step 1: Add to `IWorkflowCommandService.cs`**

```csharp
Task<SendSignalResult> SendSignal(string signalName);
```

Add result record:

```csharp
public record SendSignalResult(int DeliveredCount, List<Guid>? WorkflowInstanceIds = null);
```

**Step 2: Implement in `WorkflowCommandService.cs`**

```csharp
public async Task<SendSignalResult> SendSignal(string signalName)
{
    LogSendingSignal(signalName);

    int deliveredCount = 0;
    List<Guid>? instanceIds = null;

    // Fan-out: broadcast to running instances AND create new instances
    // Both always execute independently
    try
    {
        var signalGrain = _grainFactory.GetGrain<ISignalCorrelationGrain>(signalName);
        deliveredCount = await signalGrain.BroadcastSignal();
    }
    catch (Exception ex)
    {
        LogSignalBroadcastFailed(signalName, ex);
    }

    try
    {
        var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(signalName);
        instanceIds = await listener.FireSignalStartEvent();
        if (instanceIds.Count == 0)
            instanceIds = null;
    }
    catch (Exception ex)
    {
        LogSignalStartEventFailed(signalName, ex);
    }

    return new SendSignalResult(deliveredCount, instanceIds);
}
```

Add log methods:

```csharp
[LoggerMessage(EventId = 7005, Level = LogLevel.Information, Message = "Sending signal '{SignalName}'")]
private partial void LogSendingSignal(string signalName);

[LoggerMessage(EventId = 7006, Level = LogLevel.Error, Message = "Failed to broadcast signal '{SignalName}' to running instances")]
private partial void LogSignalBroadcastFailed(string signalName, Exception ex);

[LoggerMessage(EventId = 7007, Level = LogLevel.Error, Message = "Failed to fire signal start event for '{SignalName}'")]
private partial void LogSignalStartEventFailed(string signalName, Exception ex);
```

**Step 3: Update `SendSignalResponse.cs`**

```csharp
public record SendSignalResponse(int DeliveredCount, List<Guid>? WorkflowInstanceIds = null);
```

**Step 4: Update `WorkflowController.SendSignal`**

Replace the current implementation to delegate to command service:

```csharp
[HttpPost("signal", Name = "SendSignal")]
public async Task<IActionResult> SendSignal([FromBody] SendSignalRequest request)
{
    if (request == null || string.IsNullOrWhiteSpace(request.SignalName))
        return BadRequest(new ErrorResponse("SignalName is required"));

    try
    {
        var result = await _commandService.SendSignal(request.SignalName);

        if (result.DeliveredCount == 0 && (result.WorkflowInstanceIds == null || result.WorkflowInstanceIds.Count == 0))
            return NotFound(new ErrorResponse(
                $"No subscription or start event found for signal '{request.SignalName}'"));

        return Ok(new SendSignalResponse(result.DeliveredCount, result.WorkflowInstanceIds));
    }
    catch (Exception ex)
    {
        LogSignalDeliveryError(ex);
        return StatusCode(500, new ErrorResponse("An error occurred while broadcasting the signal"));
    }
}
```

**Step 5: Build**

```bash
dotnet build --nologo -v q
```
Expected: 0 errors

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Application/IWorkflowCommandService.cs \
  src/Fleans/Fleans.Application/WorkflowCommandService.cs \
  src/Fleans/Fleans.ServiceDefaults/DTOs/SendSignalResponse.cs \
  src/Fleans/Fleans.Api/Controllers/WorkflowController.cs
git commit -m "feat: fan-out SendSignal to both running instances and start events"
```

---

### Task 10: Integration Tests

**Files:**
- Create: `src/Fleans/Fleans.Application.Tests/SignalStartEventTests.cs`
- Modify: `src/Fleans/Fleans.Application.Tests/WorkflowTestBase.cs` — add memory storage for signal listeners

**Step 1: Add memory storage in `WorkflowTestBase.cs`**

Add to the `Configure` method:

```csharp
.AddMemoryGrainStorage(GrainStorageNames.SignalStartEventListeners)
```

**Step 2: Write integration tests**

Create `SignalStartEventTests.cs` following the `MessageStartEventTests.cs` pattern. Key tests:

1. `FireSignalStartEvent_ShouldCreateAndStartWorkflowInstance` — deploy workflow with signal start event → fire signal → instance created and started
2. `FireSignalStartEvent_TwoWorkflows_ShouldCreateBothInstances` — two processes registered for same signal → both get instantiated
3. `FireSignalStartEvent_NoRegisteredProcesses_ShouldReturnEmptyList` — fire signal for unknown name → empty list
4. `DeployWorkflow_ShouldAutoRegisterSignalStartEventListener` — deploy workflow → grain has process registered (verify via fire)
5. `Redeployment_ShouldUnregisterRemovedSignals` — deploy v1 with signal start → deploy v2 without → signal no longer creates instances
6. `SendSignal_ShouldFanOut_ToBothRunningAndStartEvents` — running instance waiting on signal catch event + signal start event registered → both receive the signal

Build BPMN test fixtures inline using the same XML-building pattern from `MessageStartEventTests.cs`, replacing `<message>` with `<signal>` and `<messageEventDefinition>` with `<signalEventDefinition>`.

**Step 3: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~SignalStartEventTests" --nologo -v q
```
Expected: All PASS

**Step 4: Run full test suite**

```bash
dotnet test --nologo -v q
```
Expected: All pass (existing + new)

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/SignalStartEventTests.cs \
  src/Fleans/Fleans.Application.Tests/WorkflowTestBase.cs
git commit -m "test: integration tests for signal start event"
```

---

### Task 11: Manual Test Fixture

**Files:**
- Create: `tests/manual/17-signal-start-event/signal-start-event.bpmn`
- Create: `tests/manual/17-signal-start-event/test-plan.md`

**Step 1: Create BPMN fixture**

Signal start event → script task → end event. Must include:
- `<signal id="sig_order" name="orderSignal"/>` at definitions level
- `<startEvent><signalEventDefinition signalRef="sig_order"/></startEvent>`
- `<scriptTask scriptFormat="csharp">` (never bare `<task>`)
- `<bpmndi:BPMNDiagram>` section (required for editor rendering)

**Step 2: Create test plan**

Follow the template from `tests/manual/16-message-start-event/test-plan.md`:
- Deploy BPMN via Web UI upload
- Send signal: `POST https://localhost:7140/Workflow/signal` with `{"SignalName":"orderSignal"}`
- Verify response includes `WorkflowInstanceIds`
- Verify instance visible in Web UI and completed
- Test unknown signal returns 404

**Step 3: Commit**

```bash
git add tests/manual/17-signal-start-event/
git commit -m "test: manual test fixture for signal start event"
```

---

### Task 12: Final Verification

**Step 1: Full build**

```bash
dotnet build --nologo -v q
```
Expected: 0 errors

**Step 2: Full test suite**

```bash
dotnet test --nologo -v q
```
Expected: All pass

**Step 3: Verify no regressions in message start event tests**

```bash
dotnet test --filter "FullyQualifiedName~MessageStartEvent" --nologo -v q
```
Expected: All existing message start event tests still pass (persistence migration didn't break anything)
