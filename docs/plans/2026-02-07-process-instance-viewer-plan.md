# Process Instance Viewer Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a Camunda Cockpit-like process instance detail page that renders the full BPMN diagram with highlighted active/completed steps.

**Architecture:** Blazor Server pages call `WorkflowEngine` which wraps Orleans grain calls. bpmn-js renders BPMN XML in the browser via JS interop. Original BPMN XML stored alongside `ProcessDefinition`. Factory grain tracks created instances per process key.

**Tech Stack:** .NET 10, Orleans 9.2.1, Blazor Server, Fluent UI, bpmn-js (CDN), JS interop

---

### Task 1: Add BpmnXml to ProcessDefinition and InstanceStateSnapshot record

**Files:**
- Modify: `src/Fleans/Fleans.Domain/ProcessDefinitions.cs`

**Step 1: Add BpmnXml property to ProcessDefinition**

In `src/Fleans/Fleans.Domain/ProcessDefinitions.cs`, add after the `Workflow` property (line 33):

```csharp
[Id(5)]
public required string BpmnXml { get; init; }
```

**Step 2: Add InstanceStateSnapshot record**

In the same file, after the `ProcessDefinitionSummary` record (after line 43), add:

```csharp
[GenerateSerializer]
public sealed record InstanceStateSnapshot(
    [property: Id(0)] List<string> ActiveActivityIds,
    [property: Id(1)] List<string> CompletedActivityIds,
    [property: Id(2)] bool IsStarted,
    [property: Id(3)] bool IsCompleted);
```

**Step 3: Add WorkflowInstanceInfo record**

In the same file, add:

```csharp
[GenerateSerializer]
public sealed record WorkflowInstanceInfo(
    [property: Id(0)] Guid InstanceId,
    [property: Id(1)] string ProcessDefinitionId,
    [property: Id(2)] bool IsStarted,
    [property: Id(3)] bool IsCompleted);
```

**Step 4: Build to verify**

Run: `dotnet build src/Fleans/`
Expected: Build errors in `WorkflowInstanceFactoryGrain` because `ProcessDefinition` now requires `BpmnXml`. This is expected — we fix it in Task 3.

---

### Task 2: Add GetStateSnapshot to WorkflowInstanceState

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/IWorkflowInstanceState.cs`
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs`

**Step 1: Add interface method**

In `IWorkflowInstanceState.cs`, add inside the interface:

```csharp
ValueTask<InstanceStateSnapshot> GetStateSnapshot();
```

Also add the using at top if not present: `using Fleans.Domain;` (it's already there via `Fleans.Domain.Activities`).

**Step 2: Implement in WorkflowInstanceState**

In `WorkflowInstanceState.cs`, add this method:

```csharp
public async ValueTask<InstanceStateSnapshot> GetStateSnapshot()
{
    var activeIds = new List<string>();
    foreach (var activity in _activeActivities)
    {
        var current = await activity.GetCurrentActivity();
        activeIds.Add(current.ActivityId);
    }

    var completedIds = new List<string>();
    foreach (var activity in _completedActivities)
    {
        var current = await activity.GetCurrentActivity();
        completedIds.Add(current.ActivityId);
    }

    return new InstanceStateSnapshot(activeIds, completedIds, _isStarted, _isCompleted);
}
```

---

### Task 3: Update Factory Grain — BpmnXml storage + Instance tracking

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs`
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs`

**Step 1: Update grain interface**

In `IWorkflowInstanceFactoryGrain.cs`, change the `DeployWorkflow` signature and add new methods:

```csharp
Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml);
Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey);
Task<string> GetBpmnXml(string processDefinitionId);
```

**Step 2: Update grain implementation**

In `WorkflowInstanceFactoryGrain.cs`:

Add new fields after `_byKey`:

```csharp
private readonly Dictionary<string, List<Guid>> _instancesByKey = new(StringComparer.Ordinal);
private readonly Dictionary<Guid, string> _instanceToDefinitionId = new();
```

Update `DeployWorkflow` to accept and store `bpmnXml`:

```csharp
public Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml)
```

In the method body, change the `ProcessDefinition` creation to include `BpmnXml = bpmnXml`.

Update `CreateWorkflowInstanceGrain` — after creating the grain and before returning, track the instance:

```csharp
var guid = Guid.NewGuid();
// ... existing code ...
TrackInstance(definition.ProcessDefinitionKey, guid, definition.ProcessDefinitionId);
return workflowInstanceGrain;
```

Same for `CreateWorkflowInstanceGrainByProcessDefinitionId`.

Add helper method:

```csharp
private void TrackInstance(string processDefinitionKey, Guid instanceId, string processDefinitionId)
{
    if (!_instancesByKey.TryGetValue(processDefinitionKey, out var instances))
    {
        instances = new List<Guid>();
        _instancesByKey[processDefinitionKey] = instances;
    }
    instances.Add(instanceId);
    _instanceToDefinitionId[instanceId] = processDefinitionId;
}
```

Implement new methods:

```csharp
public async Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey)
{
    if (!_instancesByKey.TryGetValue(processDefinitionKey, out var instanceIds))
        return Array.Empty<WorkflowInstanceInfo>();

    var result = new List<WorkflowInstanceInfo>();
    foreach (var id in instanceIds)
    {
        var instance = _grainFactory.GetGrain<IWorkflowInstance>(id);
        var state = await instance.GetState();
        var isStarted = await state.IsStarted();
        var isCompleted = await state.IsCompleted();
        var defId = _instanceToDefinitionId[id];
        result.Add(new WorkflowInstanceInfo(id, defId, isStarted, isCompleted));
    }
    return result;
}

public Task<string> GetBpmnXml(string processDefinitionId)
{
    if (!_byId.TryGetValue(processDefinitionId, out var definition))
        throw new KeyNotFoundException($"Process definition '{processDefinitionId}' not found.");
    return Task.FromResult(definition.BpmnXml);
}
```

Update the `RegisterWorkflow` method — it calls `DeployWorkflow` so it needs to pass an empty bpmnXml:

```csharp
await DeployWorkflow(def, string.Empty);
```

**Step 3: Build to verify**

Run: `dotnet build src/Fleans/`
Expected: Build errors in `WorkflowEngine.cs` because `DeployWorkflow` signature changed. Fixed in next task.

---

### Task 4: Update WorkflowEngine — thread BpmnXml + new query methods

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowEngine.cs`

**Step 1: Update DeployWorkflow**

Change signature to:

```csharp
public async Task<ProcessDefinitionSummary> DeployWorkflow(WorkflowDefinition workflow, string bpmnXml)
{
    var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
    return await factoryGrain.DeployWorkflow(workflow, bpmnXml);
}
```

**Step 2: Add new methods**

```csharp
public async Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKey(string processDefinitionKey)
{
    var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
    return await factoryGrain.GetInstancesByKey(processDefinitionKey);
}

public async Task<InstanceStateSnapshot> GetInstanceDetail(Guid instanceId)
{
    var instance = _grainFactory.GetGrain<IWorkflowInstance>(instanceId);
    var state = await instance.GetState();
    return await state.GetStateSnapshot();
}

public async Task<string> GetBpmnXml(Guid instanceId)
{
    var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
    // Look up which process definition this instance belongs to
    var instance = _grainFactory.GetGrain<IWorkflowInstance>(instanceId);
    var def = await instance.GetWorkflowDefinition();
    // We need the processDefinitionId — go through factory
    // Simpler: store it on the instance. For now, get bpmn from factory by key (latest).
    // Actually, the factory tracks instanceId → definitionId, so we add a method for that.
    return await factoryGrain.GetBpmnXmlByInstanceId(instanceId);
}
```

Wait — we need `GetBpmnXmlByInstanceId` on the factory grain. Let me revise.

**Step 2 (revised): Add GetBpmnXmlByInstanceId to grain interface and implementation**

In `IWorkflowInstanceFactoryGrain.cs`, add:

```csharp
Task<string> GetBpmnXmlByInstanceId(Guid instanceId);
```

In `WorkflowInstanceFactoryGrain.cs`, implement:

```csharp
public Task<string> GetBpmnXmlByInstanceId(Guid instanceId)
{
    if (!_instanceToDefinitionId.TryGetValue(instanceId, out var definitionId))
        throw new KeyNotFoundException($"Instance '{instanceId}' not found.");
    return GetBpmnXml(definitionId);
}
```

Then in `WorkflowEngine.cs`:

```csharp
public async Task<string> GetBpmnXml(Guid instanceId)
{
    var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
    return await factoryGrain.GetBpmnXmlByInstanceId(instanceId);
}
```

**Step 3: Build to verify**

Run: `dotnet build src/Fleans/`
Expected: Build errors in Web upload panel (calls `DeployWorkflow` with old signature). Fixed in next task.

---

### Task 5: Update Web Upload Panel — thread BpmnXml through deploy

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/WorkflowUploadPanel.razor`

**Step 1: Read the XML as string and pass it**

In `HandleFileUpload()`, around line 141-144, change from:

```csharp
using var fileStream = file.LocalFile.OpenRead();
var workflow = await BpmnConverter.ConvertFromXmlAsync(fileStream);

var deployed = await WorkflowEngine.DeployWorkflow(workflow);
```

To:

```csharp
var bpmnXml = await File.ReadAllTextAsync(file.LocalFile.FullName);
using var fileStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmnXml));
var workflow = await BpmnConverter.ConvertFromXmlAsync(fileStream);

var deployed = await WorkflowEngine.DeployWorkflow(workflow, bpmnXml);
```

**Step 2: Build to verify**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS. All compile errors resolved.

**Step 3: Run tests**

Run: `dotnet test src/Fleans/`
Expected: Some tests may fail if they call `DeployWorkflow` with old signature. Check and fix if needed.

---

### Task 6: Fix existing tests for new DeployWorkflow signature

**Step 1: Search for test usages of DeployWorkflow**

Check if any tests call `DeployWorkflow` — they may use `RegisterWorkflow` instead (which we already updated to call the new signature internally).

Run: search for `DeployWorkflow` in test files.

If tests call it directly, add `string.Empty` as second arg. If tests only use `RegisterWorkflow`, no changes needed.

**Step 2: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: PASS

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add BpmnXml storage, instance tracking, and state snapshot

Thread BPMN XML through deploy flow and store on ProcessDefinition.
Track created instances per process key in factory grain.
Add InstanceStateSnapshot for querying active/completed activity IDs.
Add WorkflowEngine query methods for instance list, detail, and BPMN XML."
```

---

### Task 7: Add bpmn-js CDN and JS interop module

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/App.razor`
- Create: `src/Fleans/Fleans.Web/wwwroot/js/bpmnViewer.js`

**Step 1: Add bpmn-js script to App.razor**

In `App.razor`, before the closing `</body>` tag, add after the blazor script:

```html
<script src="https://unpkg.com/bpmn-js@17.11.1/dist/bpmn-navigated-viewer.production.min.js"></script>
<script src="js/bpmnViewer.js"></script>
```

**Step 2: Create bpmnViewer.js**

Create `src/Fleans/Fleans.Web/wwwroot/js/bpmnViewer.js`:

```javascript
window.bpmnViewer = {
    _viewer: null,

    init: async function (containerId, bpmnXml) {
        const container = document.getElementById(containerId);
        if (!container) return;

        if (this._viewer) {
            this._viewer.destroy();
        }

        this._viewer = new BpmnJS({ container: container });

        try {
            await this._viewer.importXML(bpmnXml);
            const canvas = this._viewer.get('canvas');
            canvas.zoom('fit-viewport');
        } catch (err) {
            console.error('Failed to render BPMN diagram', err);
        }
    },

    highlight: function (completedIds, activeIds) {
        if (!this._viewer) return;

        const canvas = this._viewer.get('canvas');
        const elementRegistry = this._viewer.get('elementRegistry');

        // Clear previous markers
        elementRegistry.forEach(function (element) {
            canvas.removeMarker(element.id, 'bpmn-completed');
            canvas.removeMarker(element.id, 'bpmn-active');
        });

        // Mark completed elements
        if (completedIds) {
            completedIds.forEach(function (id) {
                if (elementRegistry.get(id)) {
                    canvas.addMarker(id, 'bpmn-completed');
                }
            });
        }

        // Mark active elements
        if (activeIds) {
            activeIds.forEach(function (id) {
                if (elementRegistry.get(id)) {
                    canvas.addMarker(id, 'bpmn-active');
                }
            });
        }
    },

    destroy: function () {
        if (this._viewer) {
            this._viewer.destroy();
            this._viewer = null;
        }
    }
};
```

**Step 3: Build to verify**

Run: `dotnet build src/Fleans/`
Expected: PASS (JS files are just static assets)

---

### Task 8: Add bpmn-js CSS styles

**Files:**
- Modify or create CSS in `src/Fleans/Fleans.Web/wwwroot/app.css` (or wherever the global CSS lives)

**Step 1: Find the CSS file**

Look for `app.css` in `wwwroot/`.

**Step 2: Add BPMN overlay styles**

Append to the CSS file:

```css
/* BPMN Viewer Styles */
.bpmn-container {
    width: 100%;
    height: 600px;
    border: 1px solid var(--neutral-stroke-rest);
    border-radius: 4px;
    overflow: hidden;
}

.bpmn-completed :deep(.djs-visual > :nth-child(1)) {
    stroke: #22c55e !important;
    fill: rgba(34, 197, 94, 0.15) !important;
}

.bpmn-active :deep(.djs-visual > :nth-child(1)) {
    stroke: #3b82f6 !important;
    fill: rgba(59, 130, 246, 0.15) !important;
    stroke-width: 3px !important;
}

:deep(.bpmn-completed .djs-visual > :nth-child(1)) {
    stroke: #22c55e !important;
    fill: rgba(34, 197, 94, 0.15) !important;
}

:deep(.bpmn-active .djs-visual > :nth-child(1)) {
    stroke: #3b82f6 !important;
    fill: rgba(59, 130, 246, 0.15) !important;
    stroke-width: 3px !important;
}
```

Note: bpmn-js uses CSS classes on SVG elements. The marker classes `bpmn-completed` and `bpmn-active` are added by our `highlight()` JS function. The `.djs-visual > :nth-child(1)` selector targets the main shape SVG element.

---

### Task 9: Create ProcessInstances page (instance list per key)

**Files:**
- Create: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstances.razor`

**Step 1: Create the page**

```razor
@page "/process-instances/{ProcessDefinitionKey}"
@rendermode InteractiveServer
@using Fleans.Application
@using Fleans.Domain
@using Microsoft.FluentUI.AspNetCore.Components
@inject WorkflowEngine WorkflowEngine
@inject NavigationManager Navigation
@inject ILogger<ProcessInstances> Logger

<PageTitle>Instances - @ProcessDefinitionKey</PageTitle>

<FluentStack Orientation="Orientation.Vertical" Gap="16px">
    <FluentStack Orientation="Orientation.Horizontal" AlignItems="AlignItems.Center" Gap="12px">
        <FluentButton Appearance="Appearance.Stealth" @onclick="GoBack">
            <FluentIcon Value="@(new Icons.Regular.Size20.ArrowLeft())" />
            Back to Workflows
        </FluentButton>
        <FluentHeader HSize="HeadingSize.H2">Instances for @ProcessDefinitionKey</FluentHeader>
    </FluentStack>

    @if (isLoading)
    {
        <FluentStack Orientation="Orientation.Vertical" AlignItems="AlignItems.Center" Gap="10px">
            <FluentProgressRing />
            <FluentBodyText>Loading instances...</FluentBodyText>
        </FluentStack>
    }
    else if (errorMessage != null)
    {
        <FluentMessageBar Intent="MessageIntent.Error" Dismissible="true" OnDismissed="@(() => errorMessage = null)">
            @errorMessage
        </FluentMessageBar>
    }
    else if (instances.Count == 0)
    {
        <FluentMessageBar Intent="MessageIntent.Info">
            No instances found for this process. Start a workflow to create one.
        </FluentMessageBar>
    }
    else
    {
        <FluentTable Items="@instances">
            <FluentTableHeader>
                <FluentTableRow>
                    <FluentTableHeaderCell>Instance ID</FluentTableHeaderCell>
                    <FluentTableHeaderCell>Status</FluentTableHeaderCell>
                    <FluentTableHeaderCell>Actions</FluentTableHeaderCell>
                </FluentTableRow>
            </FluentTableHeader>
            <FluentTableBody>
                @foreach (var instance in instances)
                {
                    <FluentTableRow>
                        <FluentTableCell>
                            <FluentBodyText>@instance.InstanceId.ToString()[..8]...</FluentBodyText>
                        </FluentTableCell>
                        <FluentTableCell>
                            @if (instance.IsCompleted)
                            {
                                <FluentBadge Color="Color.Success">Completed</FluentBadge>
                            }
                            else if (instance.IsStarted)
                            {
                                <FluentBadge Color="Color.Accent">Running</FluentBadge>
                            }
                            else
                            {
                                <FluentBadge Color="Color.Neutral">Pending</FluentBadge>
                            }
                        </FluentTableCell>
                        <FluentTableCell>
                            <FluentButton Appearance="Appearance.Stealth" @onclick="() => ViewInstance(instance.InstanceId)">
                                <FluentIcon Value="@(new Icons.Regular.Size20.Eye())" />
                                View
                            </FluentButton>
                        </FluentTableCell>
                    </FluentTableRow>
                }
            </FluentTableBody>
        </FluentTable>
    }
</FluentStack>

@code {
    [Parameter] public string ProcessDefinitionKey { get; set; } = string.Empty;

    private List<WorkflowInstanceInfo> instances = new();
    private bool isLoading = true;
    private string? errorMessage;

    protected override async Task OnInitializedAsync()
    {
        await LoadInstances();
    }

    private async Task LoadInstances()
    {
        try
        {
            isLoading = true;
            errorMessage = null;
            var result = await WorkflowEngine.GetInstancesByKey(ProcessDefinitionKey);
            instances = result.ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading instances for {Key}", ProcessDefinitionKey);
            errorMessage = $"Failed to load instances: {ex.Message}";
        }
        finally
        {
            isLoading = false;
        }
    }

    private void ViewInstance(Guid instanceId)
    {
        Navigation.NavigateTo($"/process-instance/{instanceId}");
    }

    private void GoBack()
    {
        Navigation.NavigateTo("/workflows");
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Fleans/`
Expected: PASS

---

### Task 10: Create ProcessInstance page (detail with bpmn-js)

**Files:**
- Create: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor`

**Step 1: Create the page**

```razor
@page "/process-instance/{InstanceId:guid}"
@rendermode InteractiveServer
@using Fleans.Application
@using Fleans.Domain
@using Microsoft.FluentUI.AspNetCore.Components
@inject WorkflowEngine WorkflowEngine
@inject NavigationManager Navigation
@inject IJSRuntime JS
@inject ILogger<ProcessInstance> Logger
@implements IAsyncDisposable

<PageTitle>Instance @InstanceId</PageTitle>

<FluentStack Orientation="Orientation.Vertical" Gap="16px">
    <FluentStack Orientation="Orientation.Horizontal" AlignItems="AlignItems.Center" Gap="12px">
        <FluentButton Appearance="Appearance.Stealth" @onclick="GoBack">
            <FluentIcon Value="@(new Icons.Regular.Size20.ArrowLeft())" />
            Back
        </FluentButton>
        <FluentHeader HSize="HeadingSize.H2">Process Instance</FluentHeader>
        @if (snapshot != null)
        {
            @if (snapshot.IsCompleted)
            {
                <FluentBadge Color="Color.Success">Completed</FluentBadge>
            }
            else if (snapshot.IsStarted)
            {
                <FluentBadge Color="Color.Accent">Running</FluentBadge>
            }
        }
    </FluentStack>

    <FluentBodyText><strong>Instance ID:</strong> @InstanceId</FluentBodyText>

    @if (isLoading)
    {
        <FluentStack Orientation="Orientation.Vertical" AlignItems="AlignItems.Center" Gap="10px">
            <FluentProgressRing />
            <FluentBodyText>Loading instance...</FluentBodyText>
        </FluentStack>
    }
    else if (errorMessage != null)
    {
        <FluentMessageBar Intent="MessageIntent.Error">
            @errorMessage
        </FluentMessageBar>
    }
    else
    {
        <div id="bpmn-canvas" class="bpmn-container"></div>

        <FluentStack Orientation="Orientation.Horizontal" Gap="24px" Style="align-items: flex-start;">
            <FluentStack Orientation="Orientation.Vertical" Gap="8px" Style="flex: 1;">
                <FluentHeading HSize="HeadingSize.H4">Completed Activities</FluentHeading>
                @if (snapshot!.CompletedActivityIds.Count == 0)
                {
                    <FluentBodyText>None</FluentBodyText>
                }
                else
                {
                    @foreach (var id in snapshot.CompletedActivityIds)
                    {
                        <FluentStack Orientation="Orientation.Horizontal" Gap="8px" AlignItems="AlignItems.Center">
                            <FluentIcon Value="@(new Icons.Regular.Size16.CheckmarkCircle())" Color="Color.Success" />
                            <FluentBodyText>@id</FluentBodyText>
                        </FluentStack>
                    }
                }
            </FluentStack>

            <FluentStack Orientation="Orientation.Vertical" Gap="8px" Style="flex: 1;">
                <FluentHeading HSize="HeadingSize.H4">Active Activities</FluentHeading>
                @if (snapshot!.ActiveActivityIds.Count == 0)
                {
                    <FluentBodyText>None</FluentBodyText>
                }
                else
                {
                    @foreach (var id in snapshot.ActiveActivityIds)
                    {
                        <FluentStack Orientation="Orientation.Horizontal" Gap="8px" AlignItems="AlignItems.Center">
                            <FluentIcon Value="@(new Icons.Regular.Size16.Circle())" Color="Color.Accent" />
                            <FluentBodyText>@id</FluentBodyText>
                        </FluentStack>
                    }
                }
            </FluentStack>
        </FluentStack>

        <FluentButton Appearance="Appearance.Neutral" @onclick="Refresh">
            <FluentIcon Value="@(new Icons.Regular.Size20.ArrowSync())" />
            Refresh
        </FluentButton>
    }
</FluentStack>

@code {
    [Parameter] public Guid InstanceId { get; set; }

    private InstanceStateSnapshot? snapshot;
    private string? bpmnXml;
    private bool isLoading = true;
    private string? errorMessage;

    protected override async Task OnInitializedAsync()
    {
        await LoadInstance();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && bpmnXml != null)
        {
            await RenderDiagram();
        }
    }

    private async Task LoadInstance()
    {
        try
        {
            isLoading = true;
            errorMessage = null;

            snapshot = await WorkflowEngine.GetInstanceDetail(InstanceId);
            bpmnXml = await WorkflowEngine.GetBpmnXml(InstanceId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading instance {InstanceId}", InstanceId);
            errorMessage = $"Failed to load instance: {ex.Message}";
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task RenderDiagram()
    {
        if (bpmnXml == null || snapshot == null) return;

        await JS.InvokeVoidAsync("bpmnViewer.init", "bpmn-canvas", bpmnXml);
        await JS.InvokeVoidAsync("bpmnViewer.highlight", snapshot.CompletedActivityIds, snapshot.ActiveActivityIds);
    }

    private async Task Refresh()
    {
        await LoadInstance();
        StateHasChanged();
        // Wait for render, then update diagram highlights
        await Task.Yield();
        if (snapshot != null)
        {
            await JS.InvokeVoidAsync("bpmnViewer.highlight", snapshot.CompletedActivityIds, snapshot.ActiveActivityIds);
        }
    }

    private void GoBack()
    {
        Navigation.NavigateTo("javascript:history.back()");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("bpmnViewer.destroy");
        }
        catch
        {
            // JS interop may fail during disposal
        }
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Fleans/`
Expected: PASS

---

### Task 11: Add "Instances" button to WorkflowVersionsPanel

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/WorkflowVersionsPanel.razor`
- Modify: `src/Fleans/Fleans.Web/Components/Pages/Workflows.razor`

**Step 1: Add NavigationManager and Instances button to WorkflowVersionsPanel**

Add `@inject NavigationManager Navigation` at top.

In the header area (after the "Start selected version" button area, around line 29), add an "Instances" button:

```razor
<FluentButton Appearance="Appearance.Neutral"
              @onclick="ViewInstances"
              Disabled="@(SelectedGroup == null)">
    <FluentIcon Value="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.List())" />
    <span style="margin-left: 8px;">View Instances</span>
</FluentButton>
```

In the `@code` block, add:

```csharp
private void ViewInstances()
{
    if (SelectedGroup != null)
    {
        Navigation.NavigateTo($"/process-instances/{SelectedGroup.ProcessDefinitionKey}");
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Fleans/`
Expected: PASS

---

### Task 12: Run full build and tests, commit

**Step 1: Build**

Run: `dotnet build src/Fleans/`
Expected: PASS

**Step 2: Run tests**

Run: `dotnet test src/Fleans/`
Expected: PASS

**Step 3: Commit all changes**

```bash
git add -A
git commit -m "feat: add process instance viewer with BPMN diagram visualization

- Store BPMN XML in ProcessDefinition for diagram rendering
- Track workflow instances per process key in factory grain
- Add InstanceStateSnapshot for querying active/completed activities
- Add ProcessInstances page listing instances per process key
- Add ProcessInstance detail page with bpmn-js diagram rendering
- Highlight completed (green) and active (blue) activities on diagram
- Add View Instances button to WorkflowVersionsPanel
- Add bpmn-js CDN dependency and JS interop module"
```
