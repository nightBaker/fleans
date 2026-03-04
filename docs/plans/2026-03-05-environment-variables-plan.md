# Environment Variables Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add environment variables that can be configured per-process or globally via the Web admin UI, and are injected as `Env` into workflow variable scope on start.

**Architecture:** A singleton `EnvironmentVariablesGrain` (keyed `0`) holds all env var entries. Each entry has a key, typed value (string/int/float/bool), secret flag, and optional list of process keys. On workflow start, matching variables are merged into the root scope as `Env`. A new Blazor page provides CRUD management.

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
    [Id(0)] public string Name { get; set; } = string.Empty;
    [Id(1)] public string Value { get; set; } = string.Empty;
    [Id(2)] public string ValueType { get; set; } = "string"; // "string", "int", "float", "bool"
    [Id(3)] public bool IsSecret { get; set; }
    [Id(4)] public List<string>? ProcessKeys { get; set; } // null = all processes

    /// <summary>
    /// Returns the typed value parsed from the string representation.
    /// </summary>
    public object GetTypedValue() => ValueType switch
    {
        "int" => int.Parse(Value),
        "float" => double.Parse(Value),
        "bool" => bool.Parse(Value),
        _ => Value
    };
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
feat(domain): add EnvironmentVariablesState and grain storage name
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

namespace Fleans.Application.Grains;

public interface IEnvironmentVariablesGrain : IGrainWithIntegerKey
{
    ValueTask<List<EnvironmentVariableEntry>> GetAll();
    ValueTask Set(EnvironmentVariableEntry variable);
    ValueTask Remove(string name);
    ValueTask<Dictionary<string, object>> GetVariablesForProcess(string processDefinitionKey);
}
```

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
        var existing = State.Variables.FindIndex(v => v.Name == variable.Name);
        if (existing >= 0)
            State.Variables[existing] = variable;
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
feat(application): add EnvironmentVariablesGrain with CRUD and process filtering
```

---

### Task 3: EF Core Persistence

**Files:**
- Create: `src/Fleans/Fleans.Persistence/EfCoreEnvironmentVariablesGrainStorage.cs`
- Modify: `src/Fleans/Fleans.Persistence/FleanCommandDbContext.cs`
- Modify: `src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs`
- Modify: `src/Fleans/Fleans.Persistence/DependencyInjection.cs`

**Step 1: Create the EF Core grain storage**

Follow the existing `EfCoreSignalCorrelationGrainStorage` pattern exactly. The grain is keyed by integer (`0`), so `grainId.Key.ToString()` will be `"0"`.

The `EnvironmentVariablesState` has a child collection `Variables` (list of `EnvironmentVariableEntry`). Use the same Diff pattern as `DiffSubscriptions` in the signal correlation storage.

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
        var existingByName = existing.Variables.ToDictionary(v => v.Name);
        var newNames = state.Variables.Select(v => v.Name).ToHashSet();

        // Remove deleted
        foreach (var v in existing.Variables.Where(v => !newNames.Contains(v.Name)).ToList())
            db.EnvironmentVariableEntries.Remove(v);

        // Add or update
        foreach (var v in state.Variables)
        {
            if (existingByName.TryGetValue(v.Name, out var existingVar))
            {
                db.Entry(existingVar).CurrentValues.SetValues(v);
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

Add to the end of `FleanModelConfiguration.Configure()`:

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
    entity.HasKey(e => e.Name);
    entity.Property(e => e.Name).HasMaxLength(256);
    entity.Property(e => e.Value).HasMaxLength(4000);
    entity.Property(e => e.ValueType).HasMaxLength(16);

    entity.Property(e => e.ProcessKeys)
        .HasConversion(
            v => v == null ? null : JsonConvert.SerializeObject(v),
            v => v == null ? null : JsonConvert.DeserializeObject<List<string>>(v));
});
```

Note: `EnvironmentVariableEntry` needs a `using Newtonsoft.Json;` at the top (already imported in `FleanModelConfiguration.cs`).

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

Add the helper method in the same file:

```csharp
private async Task InjectEnvironmentVariables()
{
    var processDefKey = State.ProcessDefinitionId;
    if (processDefKey is null) return;

    // Extract the process key from the definition ID (format: "key:version")
    // Look up the actual key via the workflow definition
    var definition = await GetWorkflowDefinition();
    var processKey = definition.ProcessDefinitionKey;
    if (string.IsNullOrEmpty(processKey)) return;

    var envGrain = _grainFactory.GetGrain<IEnvironmentVariablesGrain>(0);
    var envVars = await envGrain.GetVariablesForProcess(processKey);
    if (envVars.Count == 0) return;

    var envExpando = new System.Dynamic.ExpandoObject();
    var envDict = (IDictionary<string, object>)envExpando;
    envDict["Env"] = envVars;

    State.MergeState(State.VariableStates[0].Id, envExpando);
    LogEnvironmentVariablesInjected(envVars.Count);
}
```

**Step 2: Add the log message**

In `WorkflowInstance.Logging.cs`, add:

```csharp
[LoggerMessage(Level = LogLevel.Information, EventId = 1050,
    Message = "Injected {Count} environment variables into root scope")]
private partial void LogEnvironmentVariablesInjected(int count);
```

Note: Check `WorkflowInstance.Logging.cs` for the next available EventId in the 1000 range. Use an unused one.

**Step 3: Check that `IWorkflowDefinition` exposes `ProcessDefinitionKey`**

Verify that `IWorkflowDefinition` (or `WorkflowDefinition`) has a `ProcessDefinitionKey` property. If not, the `processKey` must be derived differently — perhaps from `State.ProcessDefinitionId` which stores `processDefinitionKey:version` or similar. Adjust accordingly based on what's available.

**Step 4: Build and run tests**

Run: `dotnet build && dotnet test` from `src/Fleans/`
Expected: 0 errors, all existing tests pass (no new tests yet — env grain is not activated in test cluster)

**Step 5: Commit**

```
feat(application): inject environment variables into workflow root scope on start
```

---

### Task 5: Web Admin UI — Environment Variables Page

**Files:**
- Create: `src/Fleans/Fleans.Web/Components/Pages/EnvironmentVariables.razor`
- Modify: `src/Fleans/Fleans.Web/Components/Layout/NavMenu.razor`

**Step 1: Create the Environment Variables page**

Follow the existing patterns from `Workflows.razor`:
- `@page "/environment"` with `@rendermode InteractiveServer`
- Inject `IGrainFactory` directly (Web talks to grains directly for writes)
- Inject `IWorkflowQueryService` to populate the process key selector
- Use `FluentDataGrid` to display variables
- Use `FluentDialog` or inline editing for add/edit

The page should have:
- **Data grid columns:** Name, Value (masked if IsSecret, with eye toggle), Type, Scope, Secret, Actions (edit/delete)
- **Add button** at the top
- **Edit dialog** with fields: Name (text), Value (text, type=password if secret), Type (dropdown: string/int/float/bool), Secret (checkbox), Scope (radio: All / Selected, then multi-select of process keys)

```razor
@page "/environment"
@rendermode InteractiveServer
@using Fleans.Application.Grains
@using Fleans.Application
@using Fleans.Domain.States
@inject IGrainFactory GrainFactory
@inject IWorkflowQueryService QueryService
@inject ILogger<EnvironmentVariables> Logger

<PageTitle>Environment Variables</PageTitle>

<FluentLabel Typo="Typography.PageTitle">Environment Variables</FluentLabel>

@if (loadErrorMessage is not null)
{
    <FluentMessageBar Intent="MessageIntent.Error" Dismissible="true"
                      OnDismissed="@(() => loadErrorMessage = null)">
        @loadErrorMessage
    </FluentMessageBar>
}

@if (actionSuccessMessage is not null)
{
    <FluentMessageBar Intent="MessageIntent.Success" Dismissible="true"
                      OnDismissed="@(() => actionSuccessMessage = null)">
        @actionSuccessMessage
    </FluentMessageBar>
}

<div style="margin-bottom: 12px;">
    <FluentButton Appearance="Appearance.Accent"
                  IconStart="@(new Icons.Regular.Size20.Add())"
                  @onclick="OpenAddDialog">
        Add Variable
    </FluentButton>
</div>

@if (isLoading)
{
    <FluentProgressRing />
}
else if (variables.Count == 0)
{
    <FluentMessageBar Intent="MessageIntent.Info">
        No environment variables configured.
    </FluentMessageBar>
}
else
{
    <FluentDataGrid Items="@variables.AsQueryable()" TGridItem="EnvironmentVariableEntry">
        <PropertyColumn Property="@(v => v.Name)" Title="Name" Sortable="true" />
        <TemplateColumn Title="Value">
            @if (context.IsSecret && !revealedSecrets.Contains(context.Name))
            {
                <span>••••••••</span>
                <FluentButton Appearance="Appearance.Stealth"
                              IconStart="@(new Icons.Regular.Size16.Eye())"
                              @onclick="() => revealedSecrets.Add(context.Name)"
                              Title="Reveal" />
            }
            else
            {
                <span>@context.Value</span>
                @if (context.IsSecret)
                {
                    <FluentButton Appearance="Appearance.Stealth"
                                  IconStart="@(new Icons.Regular.Size16.EyeOff())"
                                  @onclick="() => revealedSecrets.Remove(context.Name)"
                                  Title="Hide" />
                }
            }
        </TemplateColumn>
        <PropertyColumn Property="@(v => v.ValueType)" Title="Type" Sortable="true" />
        <TemplateColumn Title="Scope">
            @if (context.ProcessKeys is null)
            {
                <span>All processes</span>
            }
            else
            {
                <span>@string.Join(", ", context.ProcessKeys)</span>
            }
        </TemplateColumn>
        <TemplateColumn Title="Secret">
            @(context.IsSecret ? "Yes" : "No")
        </TemplateColumn>
        <TemplateColumn Title="Actions">
            <FluentButton Appearance="Appearance.Stealth"
                          IconStart="@(new Icons.Regular.Size20.Edit())"
                          @onclick="() => OpenEditDialog(context)"
                          Title="Edit" />
            <FluentButton Appearance="Appearance.Stealth"
                          IconStart="@(new Icons.Regular.Size20.Delete())"
                          @onclick="() => DeleteVariable(context.Name)"
                          Title="Delete" />
        </TemplateColumn>
    </FluentDataGrid>
}

@* Add/Edit Dialog *@
@if (showDialog)
{
    <FluentDialog @bind-Hidden="@dialogHidden" Modal="true" TrapFocus="true"
                  PreventDismissOnOverlayClick="true" @ondialogdismiss="CloseDialog">
        <FluentDialogHeader>
            @(isEditing ? "Edit Variable" : "Add Variable")
        </FluentDialogHeader>
        <FluentDialogBody>
            <FluentTextField Label="Name" @bind-Value="editName" Required="true"
                             ReadOnly="@isEditing" Style="width: 100%; margin-bottom: 8px;" />
            <FluentTextField Label="Value" @bind-Value="editValue" Required="true"
                             TextFieldType="@(editIsSecret ? TextFieldType.Password : TextFieldType.Text)"
                             Style="width: 100%; margin-bottom: 8px;" />
            <FluentSelect Label="Type" @bind-Value="editValueType"
                          Items="@valueTypes" OptionValue="@(v => v)" OptionText="@(v => v)"
                          Style="width: 100%; margin-bottom: 8px;" />
            <FluentCheckbox Label="Secret" @bind-Value="editIsSecret"
                            Style="margin-bottom: 8px;" />
            <FluentRadioGroup Label="Scope" @bind-Value="editScopeMode"
                              Style="margin-bottom: 8px;">
                <FluentRadio Value="@("all")">All processes</FluentRadio>
                <FluentRadio Value="@("selected")">Selected processes</FluentRadio>
            </FluentRadioGroup>
            @if (editScopeMode == "selected")
            {
                <FluentSelect Label="Processes" Multiple="true"
                              Items="@availableProcessKeys"
                              OptionValue="@(v => v)" OptionText="@(v => v)"
                              @bind-SelectedOptions="editProcessKeys"
                              Style="width: 100%; margin-bottom: 8px;" />
            }
        </FluentDialogBody>
        <FluentDialogFooter>
            <FluentButton Appearance="Appearance.Accent" @onclick="SaveVariable"
                          Loading="@isSaving">
                Save
            </FluentButton>
            <FluentButton Appearance="Appearance.Neutral" @onclick="CloseDialog">Cancel</FluentButton>
        </FluentDialogFooter>
    </FluentDialog>
}

@code {
    private List<EnvironmentVariableEntry> variables = new();
    private HashSet<string> revealedSecrets = new();
    private List<string> availableProcessKeys = new();

    private bool isLoading = true;
    private bool isSaving;
    private string? loadErrorMessage;
    private string? actionSuccessMessage;

    // Dialog state
    private bool showDialog;
    private bool dialogHidden = true;
    private bool isEditing;
    private string editName = "";
    private string editValue = "";
    private string editValueType = "string";
    private bool editIsSecret;
    private string editScopeMode = "all";
    private IEnumerable<string> editProcessKeys = Array.Empty<string>();

    private static readonly string[] valueTypes = ["string", "int", "float", "bool"];

    protected override async Task OnInitializedAsync()
    {
        await LoadData();
    }

    private async Task LoadData()
    {
        try
        {
            isLoading = true;
            var grain = GrainFactory.GetGrain<IEnvironmentVariablesGrain>(0);
            variables = await grain.GetAll();

            var definitions = await QueryService.GetAllProcessDefinitions();
            availableProcessKeys = definitions.Select(d => d.ProcessDefinitionKey).Distinct().ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading environment variables");
            loadErrorMessage = $"Failed to load: {ex.Message}";
        }
        finally
        {
            isLoading = false;
        }
    }

    private void OpenAddDialog()
    {
        isEditing = false;
        editName = "";
        editValue = "";
        editValueType = "string";
        editIsSecret = false;
        editScopeMode = "all";
        editProcessKeys = Array.Empty<string>();
        showDialog = true;
        dialogHidden = false;
    }

    private void OpenEditDialog(EnvironmentVariableEntry entry)
    {
        isEditing = true;
        editName = entry.Name;
        editValue = entry.Value;
        editValueType = entry.ValueType;
        editIsSecret = entry.IsSecret;
        editScopeMode = entry.ProcessKeys is null ? "all" : "selected";
        editProcessKeys = entry.ProcessKeys ?? Array.Empty<string>();
        showDialog = true;
        dialogHidden = false;
    }

    private void CloseDialog()
    {
        showDialog = false;
        dialogHidden = true;
    }

    private async Task SaveVariable()
    {
        try
        {
            isSaving = true;
            actionSuccessMessage = null;

            var entry = new EnvironmentVariableEntry
            {
                Name = editName,
                Value = editValue,
                ValueType = editValueType,
                IsSecret = editIsSecret,
                ProcessKeys = editScopeMode == "all" ? null : editProcessKeys.ToList()
            };

            var grain = GrainFactory.GetGrain<IEnvironmentVariablesGrain>(0);
            await grain.Set(entry);

            CloseDialog();
            actionSuccessMessage = $"Variable '{editName}' saved.";
            await LoadData();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving environment variable");
            loadErrorMessage = $"Failed to save: {ex.Message}";
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task DeleteVariable(string name)
    {
        try
        {
            actionSuccessMessage = null;
            var grain = GrainFactory.GetGrain<IEnvironmentVariablesGrain>(0);
            await grain.Remove(name);
            actionSuccessMessage = $"Variable '{name}' deleted.";
            await LoadData();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error deleting environment variable");
            loadErrorMessage = $"Failed to delete: {ex.Message}";
        }
    }
}
```

**Important notes for implementation:**
- Check that `FluentDialog`, `FluentDialogHeader`, `FluentDialogBody`, `FluentDialogFooter` exist in the Fluent UI Blazor library. If not, use `FluentDialog` with simpler slot patterns or an inline form instead.
- Check that `FluentSelect` supports `Multiple="true"` and `@bind-SelectedOptions`. If not, use `FluentListbox` or multiple checkboxes.
- Check that `FluentRadioGroup` and `FluentRadio` exist. If not, use regular HTML radio buttons or `FluentSwitch`.
- The code above is a **reference** — adapt to whatever Fluent UI components actually exist.

**Step 2: Add nav menu item**

In `NavMenu.razor`, add a new `FluentAppBarItem`:

```razor
<FluentAppBarItem Href="/environment"
                  Text="Environment"
                  IconRest="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size24.Settings())"
                  IconActive="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Filled.Size24.Settings())" />
```

Note: `Settings` icon may or may not exist. Check what icons are available. Alternatives: `Key`, `Shield`, `Options`. Pick one that exists.

**Step 3: Build and run manually**

Run: `dotnet build` from `src/Fleans/`
Expected: 0 errors

Run: `dotnet run --project Fleans.Aspire` and navigate to `/environment` in the browser. Verify:
- Page loads with empty state message
- Add a variable, see it in the grid
- Edit the variable
- Delete the variable
- Toggle secret reveal
- Test with process-scoped variables

**Step 4: Commit**

```
feat(web): add Environment Variables management page
```

---

### Task 6: Integration Test — Env Variables Injected on Start

**Files:**
- Modify or create test in: `src/Fleans/Fleans.Application.Tests/`

**Step 1: Write a test that verifies env variables are in the root scope after workflow start**

Check the existing test patterns (e.g., `ScriptTaskTests.cs`) for how they:
1. Set up a test cluster
2. Deploy a workflow definition
3. Start a workflow instance
4. Query state and assert on variables

Write a test that:
1. Sets an env variable on the `IEnvironmentVariablesGrain(0)` grain
2. Starts a simple workflow (single ScriptTask)
3. After completion, asserts that `GetVariable(rootScopeId, "Env")` returns a dictionary containing the set variable

**Important:** The test cluster may need the `EnvironmentVariablesGrain` storage registered. Check how existing tests configure grain storage — if they use in-memory storage or the full EF Core setup. You may need to register a `MemoryGrainStorage` for `GrainStorageNames.EnvironmentVariables` in the test silo config.

**Step 2: Run the test**

Run: `dotnet test --filter "EnvironmentVariables"` from `src/Fleans/`
Expected: PASS

**Step 3: Commit**

```
test: verify environment variables are injected into workflow root scope
```

---

### Task 7: Delete SQLite database and verify fresh schema

Since the project uses `EnsureCreated()` (no migrations), the new table won't be added to existing databases.

**Step 1: Note in commit message**

Add a note that existing SQLite databases must be deleted and recreated to pick up the new `EnvironmentVariables` and `EnvironmentVariableEntries` tables. This is consistent with the existing approach (no migrations).

**Step 2: Final build and full test run**

Run: `dotnet build && dotnet test` from `src/Fleans/`
Expected: 0 errors, all tests pass

**Step 3: Commit any remaining changes**

```
chore: clean up and verify full test suite passes
```
