# Environment Variables Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add environment variables that can be configured per-process or globally via the Web admin UI, and are injected as `Env` into workflow variable scope on start.

**Architecture:** A singleton `EnvironmentVariablesGrain` (keyed `0`) holds all env var entries. Each entry has a name, typed value (string/int/float/bool), secret flag, and optional list of process keys. On workflow start, matching variables are merged into the root scope as `Env`. Secret key names are tracked in `_EnvSecretKeys` for UI masking. A new Blazor page provides CRUD management with validation.

**Tech Stack:** Orleans grain + EF Core persistence, Fluent UI Blazor for the admin page.

---

### Task 1: Domain State Class

**Files:**
- Create: `src/Fleans/Fleans.Domain/States/EnvironmentVariablesState.cs`
- Modify: `src/Fleans/Fleans.Domain/GrainStorageNames.cs`

**Step 1: Create the state class**

```csharp
// src/Fleans/Fleans.Domain/States/EnvironmentVariablesState.cs
using Orleans;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class EnvironmentVariablesState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public List<EnvironmentVariableEntry> Variables { get; set; } = new();
}

[GenerateSerializer]
public class EnvironmentVariableEntry
{
    [Id(0)] public Guid Id { get; set; } = Guid.NewGuid();
    [Id(1)] public string Name { get; set; } = string.Empty;
    [Id(2)] public string Value { get; set; } = string.Empty;
    [Id(3)] public string ValueType { get; set; } = "string"; // "string", "int", "float", "bool"
    [Id(4)] public bool IsSecret { get; set; }
    [Id(5)] public List<string>? ProcessKeys { get; set; } // null = all processes

    private static readonly HashSet<string> ValidTypes = new() { "string", "int", "float", "bool" };

    /// <summary>
    /// Returns the typed value parsed from the string representation.
    /// </summary>
    public object GetTypedValue() => ValueType switch
    {
        "int" => int.Parse(Value),
        "float" => double.Parse(Value, System.Globalization.CultureInfo.InvariantCulture),
        "bool" => bool.Parse(Value),
        _ => Value
    };

    /// <summary>
    /// Validates the entry. Returns null if valid, or an error message if invalid.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Name is required.";

        if (!ValidTypes.Contains(ValueType))
            return $"Invalid type '{ValueType}'. Must be one of: string, int, float, bool.";

        return ValueType switch
        {
            "int" when !int.TryParse(Value, out _) => $"Value '{Value}' is not a valid integer.",
            "float" when !double.TryParse(Value, System.Globalization.CultureInfo.InvariantCulture, out _)
                => $"Value '{Value}' is not a valid number.",
            "bool" when !bool.TryParse(Value, out _) => $"Value '{Value}' is not a valid boolean (true/false).",
            _ => null
        };
    }
}
```

**Step 2: Add grain storage name**

In `src/Fleans/Fleans.Domain/GrainStorageNames.cs`, add:

```csharp
public const string EnvironmentVariables = "environmentVariables";
```

**Step 3: Build**

Run: `dotnet build` from `src/Fleans/`
Expected: 0 errors

**Step 4: Commit**

```
feat(domain): add EnvironmentVariablesState with validation and grain storage name
```

---

### Task 2: Grain Interface and Implementation

**Files:**
- Create: `src/Fleans/Fleans.Application/Grains/IEnvironmentVariablesGrain.cs`
- Create: `src/Fleans/Fleans.Application/Grains/EnvironmentVariablesGrain.cs`

**Step 1: Create the grain interface**

```csharp
// src/Fleans/Fleans.Application/Grains/IEnvironmentVariablesGrain.cs
using Fleans.Domain.States;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface IEnvironmentVariablesGrain : IGrainWithIntegerKey
{
    [ReadOnly] ValueTask<List<EnvironmentVariableEntry>> GetAll();
    ValueTask Set(EnvironmentVariableEntry variable);
    ValueTask Remove(string name);
    [ReadOnly] ValueTask<Dictionary<string, object>> GetVariablesForProcess(string processDefinitionKey);
    [ReadOnly] ValueTask<HashSet<string>> GetSecretKeysForProcess(string processDefinitionKey);
}
```

Note: `GetSecretKeysForProcess` returns the set of variable names marked as secret for a given process key. Used to populate `_EnvSecretKeys` in the workflow scope.

**Step 2: Create the grain implementation**

```csharp
// src/Fleans/Fleans.Application/Grains/EnvironmentVariablesGrain.cs
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class EnvironmentVariablesGrain : Grain, IEnvironmentVariablesGrain
{
    private readonly IPersistentState<EnvironmentVariablesState> _state;
    private readonly ILogger<EnvironmentVariablesGrain> _logger;

    private EnvironmentVariablesState State => _state.State;

    public EnvironmentVariablesGrain(
        [PersistentState("state", GrainStorageNames.EnvironmentVariables)]
        IPersistentState<EnvironmentVariablesState> state,
        ILogger<EnvironmentVariablesGrain> logger)
    {
        _state = state;
        _logger = logger;
    }

    public ValueTask<List<EnvironmentVariableEntry>> GetAll()
        => ValueTask.FromResult(State.Variables.ToList());

    public async ValueTask Set(EnvironmentVariableEntry variable)
    {
        var error = variable.Validate();
        if (error is not null)
            throw new ArgumentException(error);

        var existing = State.Variables.FindIndex(v => v.Name == variable.Name);
        if (existing >= 0)
        {
            variable.Id = State.Variables[existing].Id; // preserve existing Id
            State.Variables[existing] = variable;
        }
        else
            State.Variables.Add(variable);

        LogVariableSet(variable.Name, variable.ValueType, variable.IsSecret);
        await _state.WriteStateAsync();
    }

    public async ValueTask Remove(string name)
    {
        State.Variables.RemoveAll(v => v.Name == name);
        LogVariableRemoved(name);
        await _state.WriteStateAsync();
    }

    public ValueTask<Dictionary<string, object>> GetVariablesForProcess(string processDefinitionKey)
    {
        var result = new Dictionary<string, object>();
        foreach (var v in State.Variables)
        {
            if (v.ProcessKeys is null || v.ProcessKeys.Contains(processDefinitionKey))
                result[v.Name] = v.GetTypedValue();
        }
        return ValueTask.FromResult(result);
    }

    public ValueTask<HashSet<string>> GetSecretKeysForProcess(string processDefinitionKey)
    {
        var result = new HashSet<string>();
        foreach (var v in State.Variables)
        {
            if (v.IsSecret && (v.ProcessKeys is null || v.ProcessKeys.Contains(processDefinitionKey)))
                result.Add(v.Name);
        }
        return ValueTask.FromResult(result);
    }

    [LoggerMessage(Level = LogLevel.Information, EventId = 8000,
        Message = "Environment variable '{Name}' set (type={ValueType}, secret={IsSecret})")]
    private partial void LogVariableSet(string name, string valueType, bool isSecret);

    [LoggerMessage(Level = LogLevel.Information, EventId = 8001,
        Message = "Environment variable '{Name}' removed")]
    private partial void LogVariableRemoved(string name);
}
```

**Step 3: Build**

Run: `dotnet build` from `src/Fleans/`
Expected: 0 errors

**Step 4: Commit**

```
feat(application): add EnvironmentVariablesGrain with validation and secret key tracking
```

---

### Task 3: EF Core Persistence

**Files:**
- Create: `src/Fleans/Fleans.Persistence/EfCoreEnvironmentVariablesGrainStorage.cs`
- Modify: `src/Fleans/Fleans.Persistence/FleanCommandDbContext.cs`
- Modify: `src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs`
- Modify: `src/Fleans/Fleans.Persistence/DependencyInjection.cs`

**Step 1: Create the EF Core grain storage**

Follow the existing `EfCoreSignalCorrelationGrainStorage` pattern. The grain is keyed by integer (`0`), so `grainId.Key.ToString()` will be `"0"`. Use the Diff pattern from `DiffSubscriptions` for the child `Variables` collection. Key the diff by `Id` (Guid), not by `Name`.

```csharp
// src/Fleans/Fleans.Persistence/EfCoreEnvironmentVariablesGrainStorage.cs
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreEnvironmentVariablesGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreEnvironmentVariablesGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
        => _dbContextFactory = dbContextFactory;

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = await db.EnvironmentVariables
            .Include(e => e.Variables)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == id);
        if (state is not null)
        {
            grainState.State = (T)(object)state;
            grainState.ETag = state.ETag;
            grainState.RecordExists = true;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = (EnvironmentVariablesState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.EnvironmentVariables
            .Include(e => e.Variables)
            .FirstOrDefaultAsync(e => e.Key == id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.EnvironmentVariables.Add(state);
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.Key).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = newETag;

            DiffVariables(db, existing, state);
        }

        await db.SaveChangesAsync();
        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var existing = await db.EnvironmentVariables.FindAsync(id);
        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");
            db.EnvironmentVariables.Remove(existing);
            await db.SaveChangesAsync();
        }
        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private static void DiffVariables(
        FleanCommandDbContext db,
        EnvironmentVariablesState existing,
        EnvironmentVariablesState state)
    {
        var existingById = existing.Variables.ToDictionary(v => v.Id);
        var newIds = state.Variables.Select(v => v.Id).ToHashSet();

        // Remove deleted
        foreach (var v in existing.Variables.Where(v => !newIds.Contains(v.Id)).ToList())
            db.EnvironmentVariableEntries.Remove(v);

        // Add or update
        foreach (var v in state.Variables)
        {
            if (existingById.TryGetValue(v.Id, out var existingVar))
            {
                db.Entry(existingVar).CurrentValues.SetValues(v);
                db.Entry(existingVar).Property(e => e.Id).IsModified = false;
            }
            else
            {
                existing.Variables.Add(v);
            }
        }
    }
}
```

**Step 2: Add DbSets to FleanCommandDbContext**

Add these two lines to `FleanCommandDbContext.cs` alongside other DbSets:

```csharp
public DbSet<EnvironmentVariablesState> EnvironmentVariables => Set<EnvironmentVariablesState>();
public DbSet<EnvironmentVariableEntry> EnvironmentVariableEntries => Set<EnvironmentVariableEntry>();
```

**Step 3: Add entity configuration to FleanModelConfiguration**

Add to the end of `FleanModelConfiguration.Configure()`. Note: `EnvironmentVariableEntry` uses `Guid Id` as PK with a unique index on `Name`:

```csharp
modelBuilder.Entity<EnvironmentVariablesState>(entity =>
{
    entity.ToTable("EnvironmentVariables");
    entity.HasKey(e => e.Key);
    entity.Property(e => e.Key).HasMaxLength(64);
    entity.Property(e => e.ETag).HasMaxLength(64);

    entity.HasMany(e => e.Variables)
        .WithOne()
        .OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<EnvironmentVariableEntry>(entity =>
{
    entity.ToTable("EnvironmentVariableEntries");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Name).HasMaxLength(256);
    entity.HasIndex(e => e.Name).IsUnique();
    entity.Property(e => e.Value).HasMaxLength(4000);
    entity.Property(e => e.ValueType).HasMaxLength(16);

    entity.Property(e => e.ProcessKeys)
        .HasConversion(
            v => v == null ? null : JsonConvert.SerializeObject(v),
            v => v == null ? null : JsonConvert.DeserializeObject<List<string>>(v));
});
```

**Step 4: Register storage in DependencyInjection.cs**

Add to `AddEfCorePersistence`:

```csharp
services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.EnvironmentVariables,
    (sp, _) => new EfCoreEnvironmentVariablesGrainStorage(
        sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));
```

**Step 5: Build**

Run: `dotnet build` from `src/Fleans/`
Expected: 0 errors

**Step 6: Commit**

```
feat(persistence): add EF Core storage for EnvironmentVariablesGrain
```

---

### Task 4: Inject Env Variables on Workflow Start

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Execution.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs`

**Step 1: Add env injection to StartWorkflow**

In `WorkflowInstance.Execution.cs`, modify `StartWorkflow()` to inject env variables into root scope between `State.Start()` and `ExecuteWorkflow()`:

```csharp
public async Task StartWorkflow()
{
    await EnsureWorkflowDefinitionAsync();
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();
    LogWorkflowStarted();
    State.Start();

    // Inject environment variables as "Env" in root scope
    await InjectEnvironmentVariables();

    await ExecuteWorkflow();
    await _state.WriteStateAsync();
}
```

Add the helper method in the same file. **Important:** `IWorkflowDefinition` does NOT have `ProcessDefinitionKey` — use `WorkflowId` which is the BPMN process id (same as the process definition key).

```csharp
private async Task InjectEnvironmentVariables()
{
    var definition = await GetWorkflowDefinition();
    var processKey = definition.WorkflowId;
    if (string.IsNullOrEmpty(processKey)) return;

    var envGrain = _grainFactory.GetGrain<IEnvironmentVariablesGrain>(0);
    var envVars = await envGrain.GetVariablesForProcess(processKey);
    if (envVars.Count == 0) return;

    var envExpando = new System.Dynamic.ExpandoObject();
    var envDict = (IDictionary<string, object>)envExpando;
    envDict["Env"] = envVars;

    // Track secret key names for UI masking
    var secretKeys = await envGrain.GetSecretKeysForProcess(processKey);
    if (secretKeys.Count > 0)
        envDict["_EnvSecretKeys"] = secretKeys.ToList();

    State.MergeState(State.VariableStates[0].Id, envExpando);
    LogEnvironmentVariablesInjected(envVars.Count);
}
```

**Step 2: Add the log message**

In `WorkflowInstance.Logging.cs`, add (check for next available EventId in the 1000 range):

```csharp
[LoggerMessage(Level = LogLevel.Information, EventId = 1050,
    Message = "Injected {Count} environment variables into root scope")]
private partial void LogEnvironmentVariablesInjected(int count);
```

**Step 3: Build and run tests**

Run: `dotnet build && dotnet test` from `src/Fleans/`
Expected: 0 errors, all existing tests pass

**Step 4: Commit**

```
feat(application): inject environment variables into workflow root scope on start
```

---

### Task 5: Mask Secrets in Process Instance Variables Tab

**Files:**
- Modify: `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs`
- Modify: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor`

**Step 1: Mask secret env values in WorkflowQueryService**

In `WorkflowQueryService.GetStateSnapshot`, when building `variableStates`, check if a `_EnvSecretKeys` variable exists in the same scope. If `Env` is a dictionary and `_EnvSecretKeys` is a list, mask the values for keys in that list.

Alternatively, handle masking in the Razor component: in `ProcessInstance.razor`'s `GetVariableRows`, when rendering the `Env` key's value, check the same scope for `_EnvSecretKeys` and replace matching sub-values with `••••••••`.

**Recommended approach:** Handle in `ProcessInstance.razor` since it's a UI concern. The `WorkflowQueryService` should return accurate data — masking belongs in the view layer.

**Step 2: Hide `_EnvSecretKeys` from the variable display**

In `GetVariableRows`, filter out keys starting with `_Env` so internal tracking variables don't clutter the UI.

**Step 3: Build**

Run: `dotnet build` from `src/Fleans/`
Expected: 0 errors

**Step 4: Commit**

```
feat(web): mask secret environment variable values in process instance variables tab
```

---

### Task 6: Web Admin UI — Environment Variables Page

**Files:**
- Create: `src/Fleans/Fleans.Web/Components/Pages/EnvironmentVariables.razor`
- Modify: `src/Fleans/Fleans.Web/Components/Layout/NavMenu.razor`

**Step 0: Verify Fluent UI components exist**

Before writing any Razor code, verify these components exist in the Fluent UI Blazor library at https://www.fluentui-blazor.net/:
- `FluentDialog` with `FluentDialogHeader`, `FluentDialogBody`, `FluentDialogFooter`
- `FluentSelect` with `Multiple="true"` and `@bind-SelectedOptions`
- `FluentRadioGroup` and `FluentRadio`
- `FluentTextField` with `TextFieldType.Password`

If any don't exist, use alternatives. The code below is a **reference** — adapt to what actually exists.

**Step 1: Create the Environment Variables page**

Follow the existing patterns from `Workflows.razor`:
- `@page "/environment"` with `@rendermode InteractiveServer`
- Inject `IGrainFactory` directly (Web talks to grains directly for writes)
- Inject `IWorkflowQueryService` to populate the process key selector
- Use `FluentDataGrid` to display variables
- Use dialog or inline form for add/edit

The page must include:
- **Data grid columns:** Name, Value (masked for secrets, eye toggle to reveal), Type, Scope, Secret, Actions (edit/delete)
- **Add button** at the top
- **Add/Edit form** with fields: Name (text), Value (text, password input if secret), Type (dropdown: string/int/float/bool), Secret (checkbox), Scope (radio: All / Selected, then multi-select of process keys)
- **Client-side validation:** call `EnvironmentVariableEntry.Validate()` locally before calling the grain, show inline error. The grain also validates server-side. Catch `ArgumentException` from the grain and display it.

**Step 2: Add nav menu item**

In `NavMenu.razor`, add a new `FluentAppBarItem`. Check available icons — candidates: `Settings`, `Key`, `Options`. Pick one that exists.

```razor
<FluentAppBarItem Href="/environment"
                  Text="Environment"
                  IconRest="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size24.Settings())"
                  IconActive="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Filled.Size24.Settings())" />
```

**Step 3: Build**

Run: `dotnet build` from `src/Fleans/`
Expected: 0 errors

**Step 4: Commit**

```
feat(web): add Environment Variables management page with validation
```

---

### Task 7: Tests — Validation and Filtering Logic

**Files:**
- Create: `src/Fleans/Fleans.Domain.Tests/EnvironmentVariableEntryTests.cs` (or appropriate test project)

**Step 1: Write unit tests for EnvironmentVariableEntry.Validate()**

```csharp
[TestMethod] public void Validate_ValidString_ReturnsNull()
[TestMethod] public void Validate_ValidInt_ReturnsNull()
[TestMethod] public void Validate_ValidFloat_ReturnsNull()
[TestMethod] public void Validate_ValidBool_ReturnsNull()
[TestMethod] public void Validate_EmptyName_ReturnsError()
[TestMethod] public void Validate_InvalidType_ReturnsError()
[TestMethod] public void Validate_IntWithNonNumericValue_ReturnsError()
[TestMethod] public void Validate_FloatWithNonNumericValue_ReturnsError()
[TestMethod] public void Validate_BoolWithNonBoolValue_ReturnsError()
```

**Step 2: Run tests**

Run: `dotnet test --filter "EnvironmentVariableEntry"` from `src/Fleans/`
Expected: all PASS

**Step 3: Commit**

```
test: add validation tests for EnvironmentVariableEntry
```

---

### Task 8: Integration Test — Env Variables Injected on Start

**Files:**
- Create test in: `src/Fleans/Fleans.Application.Tests/EnvironmentVariablesTests.cs`

**Step 1: Register EnvironmentVariables storage in test cluster**

Check how the test cluster is configured (look at `TestSiloConfigurations.cs` or the `TestCluster` setup). Add `MemoryGrainStorage` for `GrainStorageNames.EnvironmentVariables` if needed:

```csharp
siloBuilder.AddMemoryGrainStorage(GrainStorageNames.EnvironmentVariables);
```

**Step 2: Write tests**

Test 1: **Global env var is injected on workflow start**
1. Set a global env variable (ProcessKeys = null) on `IEnvironmentVariablesGrain(0)`
2. Start a simple workflow (single ScriptTask that completes)
3. After completion, get state and assert `Env` variable exists in root scope with the correct value

Test 2: **Process-scoped env var is only injected for matching process**
1. Set an env variable with `ProcessKeys = ["other-process"]`
2. Start a workflow with a different process key
3. Assert `Env` variable does NOT contain that variable

Test 3: **Secret keys are tracked in _EnvSecretKeys**
1. Set a secret env variable
2. Start a workflow
3. Assert `_EnvSecretKeys` contains the secret variable name

Test 4: **Validation rejects invalid entries**
1. Call `Set` with an int-typed variable whose value is "abc"
2. Assert `ArgumentException` is thrown

**Step 3: Run tests**

Run: `dotnet test --filter "EnvironmentVariables"` from `src/Fleans/`
Expected: all PASS

**Step 4: Commit**

```
test: add integration tests for environment variable injection and validation
```

---

### Task 9: Final Verification

**Step 1: Full build and test**

Run: `dotnet build && dotnet test` from `src/Fleans/`
Expected: 0 errors, all tests pass

**Step 2: Note about SQLite schema**

Existing SQLite databases must be deleted and recreated to pick up the new `EnvironmentVariables` and `EnvironmentVariableEntries` tables. This is consistent with the existing approach (no migrations, `EnsureCreated()` at startup).

**Step 3: Commit any remaining cleanup**

```
chore: final cleanup and verification
```
