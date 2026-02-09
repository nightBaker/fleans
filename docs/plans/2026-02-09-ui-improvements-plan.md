# UI Improvements Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Five UI improvements: compact FluentAppBar navigation, vertically resizable BPMN viewer, read-only properties panel on instance detail page, edit any version of BPMN, and version-filtered instances.

**Architecture:** Replace the 250px FluentNavMenu with a ~48px FluentAppBar. Add drag-handle resize (vertical for BPMN canvas, horizontal for properties panel) via a shared JS utility. Add `ReadOnly` parameter to `ElementPropertiesPanel`. Add two new backend methods (`GetBpmnXmlByKeyAndVersion`, `GetInstancesByKeyAndVersion`) and wire them through to the UI with new route parameters.

**Tech Stack:** .NET 10, Blazor Server, Fluent UI Blazor, bpmn.js (navigated-viewer), Orleans 9.2.1, JavaScript interop

---

### Task 1: FluentAppBar Navigation

Replace the 250px `FluentNavMenu` sidebar with a compact vertical `FluentAppBar`.

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Layout/MainLayout.razor`
- Delete content from: `src/Fleans/Fleans.Web/Components/Layout/NavMenu.razor` (keep empty file or remove)

**Step 1: Update MainLayout.razor**

Replace the current `FluentStack` + `FluentNavMenu` block with a `FluentAppBar`:

```razor
@inherits LayoutComponentBase

<FluentLayout>
    <FluentHeader>
        Fleans.Web
        <FluentSpacer />
    </FluentHeader>

    <FluentStack Orientation="Orientation.Horizontal" Width="100%">
        <FluentAppBar Style="height: calc(100vh - 50px);">
            <FluentAppBarItem Href="/workflows"
                              Text="Workflows"
                              IconRest="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size24.Flow())" />
            <FluentAppBarItem Href="/editor"
                              Text="Editor"
                              IconRest="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size24.DrawShape())" />
        </FluentAppBar>

        <FluentBodyContent Style="padding: calc(var(--design-unit) * 1px) 0">
            @Body
        </FluentBodyContent>
    </FluentStack>
</FluentLayout>

<div id="blazor-error-ui" data-nosnippet>
    An unhandled error has occurred.
    <a href="." class="reload">Reload</a>
    <span class="dismiss">🗙</span>
</div>
```

**Step 2: Empty out NavMenu.razor**

The NavMenu component is no longer used. Replace its contents with an empty file (or remove `<NavMenu/>` reference from MainLayout — already done in step 1).

```razor
@* Navigation moved to FluentAppBar in MainLayout *@
```

**Step 3: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Layout/MainLayout.razor src/Fleans/Fleans.Web/Components/Layout/NavMenu.razor
git commit -m "feat: replace FluentNavMenu with compact FluentAppBar navigation"
```

---

### Task 2: Backend — GetBpmnXmlByKeyAndVersion & GetInstancesByKeyAndVersion

Add two new grain methods and wire them through `WorkflowEngine`.

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs`
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs`
- Modify: `src/Fleans/Fleans.Application/WorkflowEngine.cs`

**Step 1: Add interface methods**

In `IWorkflowInstanceFactoryGrain.cs`, add after the existing `GetBpmnXmlByInstanceId` method:

```csharp
Task<string> GetBpmnXmlByKeyAndVersion(string processDefinitionKey, int version);
Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string processDefinitionKey, int version);
```

**Step 2: Implement grain methods**

In `WorkflowInstanceFactoryGrain.cs`, add after the existing `GetBpmnXmlByInstanceId` method:

```csharp
public Task<string> GetBpmnXmlByKeyAndVersion(string processDefinitionKey, int version)
{
    if (!_byKey.TryGetValue(processDefinitionKey, out var versions) || versions.Count == 0)
        throw new KeyNotFoundException($"Process definition key '{processDefinitionKey}' not found.");

    var definition = versions.FirstOrDefault(v => v.Version == version)
        ?? throw new KeyNotFoundException($"Version {version} of '{processDefinitionKey}' not found.");

    return Task.FromResult(definition.BpmnXml);
}

public async Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string processDefinitionKey, int version)
{
    if (!_byKey.TryGetValue(processDefinitionKey, out var versions) || versions.Count == 0)
        return Array.Empty<WorkflowInstanceInfo>();

    var definition = versions.FirstOrDefault(v => v.Version == version);
    if (definition == null)
        return Array.Empty<WorkflowInstanceInfo>();

    var targetDefinitionId = definition.ProcessDefinitionId;

    if (!_instancesByKey.TryGetValue(processDefinitionKey, out var instanceIds))
        return Array.Empty<WorkflowInstanceInfo>();

    var result = new List<WorkflowInstanceInfo>();
    foreach (var id in instanceIds)
    {
        if (_instanceToDefinitionId.TryGetValue(id, out var defId) && defId == targetDefinitionId)
        {
            var instance = _grainFactory.GetGrain<IWorkflowInstance>(id);
            var info = await instance.GetInstanceInfo();
            result.Add(info);
        }
    }
    return result;
}
```

**Step 3: Add WorkflowEngine facade methods**

In `WorkflowEngine.cs`, add after the existing `GetBpmnXmlByKey` method:

```csharp
public async Task<string> GetBpmnXmlByKeyAndVersion(string processDefinitionKey, int version)
{
    var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
    return await factoryGrain.GetBpmnXmlByKeyAndVersion(processDefinitionKey, version);
}

public async Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string processDefinitionKey, int version)
{
    var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
    return await factoryGrain.GetInstancesByKeyAndVersion(processDefinitionKey, version);
}
```

**Step 4: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 5: Run tests**

Run: `dotnet test src/Fleans/`
Expected: All existing tests pass

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs src/Fleans/Fleans.Application/WorkflowEngine.cs
git commit -m "feat: add GetBpmnXmlByKeyAndVersion and GetInstancesByKeyAndVersion"
```

---

### Task 3: Edit Any Version of BPMN

Add a versioned route to the Editor page and update the Workflows page to link to it.

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/Editor.razor`
- Modify: `src/Fleans/Fleans.Web/Components/Pages/Workflows.razor`

**Step 1: Add versioned route and parameter to Editor.razor**

Add a new `@page` directive and `Version` parameter. Update `OnAfterRenderAsync` to load the specific version when provided.

Add the route directive after the existing ones:

```razor
@page "/editor"
@page "/editor/{ProcessDefinitionKey}"
@page "/editor/{ProcessDefinitionKey}/{Version:int}"
```

Add the parameter in the `@code` block after `ProcessDefinitionKey`:

```csharp
[Parameter] public int? Version { get; set; }
```

In `OnAfterRenderAsync`, replace the BPMN loading block (lines 130-144) with version-aware loading:

```csharp
if (!string.IsNullOrEmpty(ProcessDefinitionKey))
{
    try
    {
        string bpmnXml;
        if (Version.HasValue)
        {
            bpmnXml = await WorkflowEngine.GetBpmnXmlByKeyAndVersion(ProcessDefinitionKey, Version.Value);
            processKey = ProcessDefinitionKey;
        }
        else
        {
            bpmnXml = await WorkflowEngine.GetBpmnXmlByKey(ProcessDefinitionKey);
            processKey = ProcessDefinitionKey;
        }
        await JS.InvokeVoidAsync("bpmnEditor.loadXml", bpmnXml);
        StateHasChanged();
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Failed to load BPMN for key {Key}", ProcessDefinitionKey);
        errorMessage = $"Failed to load workflow: {ex.Message}";
        StateHasChanged();
    }
}
```

Update the toolbar badge to show the version when editing a specific version. Replace the existing badge block (lines 24-27):

```razor
@if (!string.IsNullOrEmpty(processKey))
{
    @if (Version.HasValue)
    {
        <FluentBadge Color="Color.Accent">Editing v@(Version) of @processKey</FluentBadge>
    }
    else
    {
        <FluentBadge Color="Color.Accent">@processKey</FluentBadge>
    }
}
```

**Step 2: Update Workflows.razor Edit button**

Change `EditWorkflow` to navigate to the version-specific editor URL. Replace the method:

```csharp
private void EditWorkflow(ProcessDefinitionSummary definition)
{
    Navigation.NavigateTo($"/editor/{Uri.EscapeDataString(definition.ProcessDefinitionKey)}/{definition.Version}");
}
```

**Step 3: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/Editor.razor src/Fleans/Fleans.Web/Components/Pages/Workflows.razor
git commit -m "feat: allow editing any version of BPMN from workflows page"
```

---

### Task 4: Instances Filtered by Version

Add a versioned route to the ProcessInstances page and update the Workflows page to link to it.

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstances.razor`
- Modify: `src/Fleans/Fleans.Web/Components/Pages/Workflows.razor`

**Step 1: Add versioned route and parameter to ProcessInstances.razor**

Add a new `@page` directive:

```razor
@page "/process-instances/{ProcessDefinitionKey}"
@page "/process-instances/{ProcessDefinitionKey}/{Version:int}"
```

Add the `Version` parameter in the `@code` block:

```csharp
[Parameter] public int? Version { get; set; }
```

Update the page header to show version info. Replace the `PageHeader` line:

```razor
<PageHeader Title="@GetPageTitle()" GoBackUrl="/workflows" />
```

Add the helper method in the `@code` block:

```csharp
private string GetPageTitle()
{
    if (Version.HasValue)
        return $"Instances — {ProcessDefinitionKey} v{Version}";
    return $"Instances — {ProcessDefinitionKey} (all versions)";
}
```

Update `LoadInstances` to call version-specific method when `Version` is provided:

```csharp
private async Task LoadInstances()
{
    try
    {
        isLoading = true;
        errorMessage = null;
        IReadOnlyList<WorkflowInstanceInfo> result;
        if (Version.HasValue)
            result = await WorkflowEngine.GetInstancesByKeyAndVersion(ProcessDefinitionKey, Version.Value);
        else
            result = await WorkflowEngine.GetInstancesByKey(ProcessDefinitionKey);
        instances = result.ToList();
        instancesQueryable = instances.AsQueryable();
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
```

**Step 2: Update Workflows.razor Instances button**

Change `ViewInstances` to navigate with version:

```csharp
private void ViewInstances(ProcessDefinitionSummary version)
{
    Navigation.NavigateTo($"/process-instances/{Uri.EscapeDataString(version.ProcessDefinitionKey)}/{version.Version}");
}
```

**Step 3: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/ProcessInstances.razor src/Fleans/Fleans.Web/Components/Pages/Workflows.razor
git commit -m "feat: filter instances by version from workflows page"
```

---

### Task 5: Drag Handle Resize JS Utility

Create a small JS utility for drag-handle resizing, used by both vertical (BPMN canvas) and horizontal (properties panel) resizers.

**Files:**
- Create: `src/Fleans/Fleans.Web/wwwroot/js/dragResize.js`
- Modify: `src/Fleans/Fleans.Web/Components/App.razor` (add script reference)

**Step 1: Create dragResize.js**

```javascript
window.dragResize = {
    /**
     * Attach a vertical drag-resize handler.
     * @param {string} handleId - The drag handle element ID
     * @param {string} topId - The top panel element ID (gets height adjusted)
     * @param {number} minHeight - Minimum height in px
     * @param {number} maxHeight - Maximum height in px
     * @param {object} dotNetRef - Blazor .NET reference for callbacks
     * @param {string} callbackMethod - Method name to invoke on drag end with new height
     */
    initVertical: function (handleId, topId, minHeight, maxHeight, dotNetRef, callbackMethod) {
        var handle = document.getElementById(handleId);
        var topPanel = document.getElementById(topId);
        if (!handle || !topPanel) return;

        var startY = 0;
        var startHeight = 0;

        function onMouseDown(e) {
            e.preventDefault();
            startY = e.clientY;
            startHeight = topPanel.getBoundingClientRect().height;
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
            document.body.style.cursor = 'row-resize';
            document.body.style.userSelect = 'none';
        }

        function onMouseMove(e) {
            var delta = e.clientY - startY;
            var newHeight = Math.min(Math.max(startHeight + delta, minHeight), maxHeight);
            topPanel.style.height = newHeight + 'px';
        }

        function onMouseUp(e) {
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            var finalHeight = topPanel.getBoundingClientRect().height;
            if (dotNetRef && callbackMethod) {
                dotNetRef.invokeMethodAsync(callbackMethod, finalHeight);
            }
        }

        handle.addEventListener('mousedown', onMouseDown);
    },

    /**
     * Attach a horizontal drag-resize handler.
     * @param {string} handleId - The drag handle element ID
     * @param {string} panelId - The right panel element ID (gets width adjusted)
     * @param {number} minWidth - Minimum width in px
     * @param {number} maxWidth - Maximum width in px
     * @param {object} dotNetRef - Blazor .NET reference for callbacks
     * @param {string} callbackMethod - Method name to invoke on drag end with new width
     */
    initHorizontal: function (handleId, panelId, minWidth, maxWidth, dotNetRef, callbackMethod) {
        var handle = document.getElementById(handleId);
        var panel = document.getElementById(panelId);
        if (!handle || !panel) return;

        var startX = 0;
        var startWidth = 0;

        function onMouseDown(e) {
            e.preventDefault();
            startX = e.clientX;
            startWidth = panel.getBoundingClientRect().width;
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
        }

        function onMouseMove(e) {
            // Dragging left increases panel width (panel is on the right)
            var delta = startX - e.clientX;
            var newWidth = Math.min(Math.max(startWidth + delta, minWidth), maxWidth);
            panel.style.width = newWidth + 'px';
        }

        function onMouseUp(e) {
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            var finalWidth = panel.getBoundingClientRect().width;
            if (dotNetRef && callbackMethod) {
                dotNetRef.invokeMethodAsync(callbackMethod, finalWidth);
            }
        }

        handle.addEventListener('mousedown', onMouseDown);
    }
};
```

**Step 2: Add script reference to App.razor**

Find `App.razor` and add the script tag after the existing bpmn JS scripts:

```html
<script src="js/dragResize.js"></script>
```

**Step 3: Add CSS for drag handles to app.css**

Append to `src/Fleans/Fleans.Web/wwwroot/app.css`:

```css
/* Drag Resize Handles */
.drag-handle-vertical {
    height: 6px;
    cursor: row-resize;
    background: var(--neutral-stroke-rest);
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
}

.drag-handle-vertical::after {
    content: '';
    width: 40px;
    height: 2px;
    background: var(--neutral-foreground-hint);
    border-radius: 1px;
}

.drag-handle-horizontal {
    width: 6px;
    cursor: col-resize;
    background: var(--neutral-stroke-rest);
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
}

.drag-handle-horizontal::after {
    content: '';
    height: 40px;
    width: 2px;
    background: var(--neutral-foreground-hint);
    border-radius: 1px;
}
```

**Step 4: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Web/wwwroot/js/dragResize.js src/Fleans/Fleans.Web/wwwroot/app.css src/Fleans/Fleans.Web/Components/App.razor
git commit -m "feat: add drag handle resize JS utility and CSS"
```

---

### Task 6: Vertically Resizable BPMN Viewer

Replace the fixed 600px `.bpmn-container` on the ProcessInstance page with a drag-handle resizable layout.

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor`
- Modify: `src/Fleans/Fleans.Web/wwwroot/app.css`

**Step 1: Update ProcessInstance.razor markup**

Replace the single `<div id="bpmn-canvas" class="bpmn-container"></div>` line (line 49) and everything through the closing `</FluentTabs>` with a resizable layout. The new layout wraps the canvas and tabs in a flex column with a drag handle between them:

Replace this section (the `else` block content after the error check, lines 48-217):

```razor
    else
    {
        <div class="instance-content">
            <div class="instance-main">
                <div id="bpmn-canvas" class="bpmn-container" style="height: @(canvasHeight)px;"></div>

                <div id="vertical-drag-handle" class="drag-handle-vertical"></div>

                <FluentButton Appearance="Appearance.Neutral"
                              IconStart="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.ArrowSync())"
                              Loading="@isRefreshing"
                              @onclick="Refresh">
                    Refresh
                </FluentButton>

                @if (failedActivities.Count > 0)
                {
                    @foreach (var failed in failedActivities)
                    {
                        <FluentMessageBar Intent="MessageIntent.Error">
                            Activity <strong>@failed.ActivityId</strong> failed: [@failed.ErrorState!.Code] @failed.ErrorState.Message
                        </FluentMessageBar>
                    }
                }

                <FluentTabs>
                    <FluentTab Label="Activities">
                        <FluentDataGrid Items="@filteredActivities" GridTemplateColumns="1fr 1fr 1fr 1fr 1fr 1fr 2fr"
                                        TGridItem="ActivityInstanceSnapshot"
                                        OnRowClick="@OnActivityRowClick"
                                        RowClass="@GetActivityRowClass">
                            <PropertyColumn Property="@(a => a.ActivityId)" Title="Activity ID" Sortable="true">
                                <ColumnOptions>
                                    <FluentSearch @bind-Value="activityIdFilter"
                                                  @bind-Value:after="ApplyActivityFilters"
                                                  Placeholder="Filter..." />
                                </ColumnOptions>
                            </PropertyColumn>
                            <PropertyColumn Property="@(a => a.ActivityType)" Title="Type" Sortable="true">
                                <ColumnOptions>
                                    <FluentSearch @bind-Value="activityTypeFilter"
                                                  @bind-Value:after="ApplyActivityFilters"
                                                  Placeholder="Filter..." />
                                </ColumnOptions>
                            </PropertyColumn>
                            <TemplateColumn Title="Status" SortBy="@statusSort">
                                <ColumnOptions>
                                    <FluentSearch @bind-Value="activityStatusFilter"
                                                  @bind-Value:after="ApplyActivityFilters"
                                                  Placeholder="Filter..." />
                                </ColumnOptions>
                                <ChildContent>
                                    @if (context.ErrorState != null)
                                    {
                                        <FluentBadge Color="Color.Error">Failed</FluentBadge>
                                    }
                                    else if (context.IsCompleted)
                                    {
                                        <FluentBadge Color="Color.Success">Completed</FluentBadge>
                                    }
                                    else if (context.IsExecuting)
                                    {
                                        <FluentBadge Color="Color.Accent">Running</FluentBadge>
                                    }
                                    else
                                    {
                                        <FluentBadge Color="Color.Lightweight">Pending</FluentBadge>
                                    }
                                </ChildContent>
                            </TemplateColumn>
                            <PropertyColumn Property="@(a => a.CreatedAt)" Title="Created At" Sortable="true" Format="yyyy-MM-dd HH:mm:ss" />
                            <PropertyColumn Property="@(a => a.ExecutionStartedAt)" Title="Started At" Sortable="true" Format="yyyy-MM-dd HH:mm:ss" />
                            <PropertyColumn Property="@(a => a.CompletedAt)" Title="Completed At" Sortable="true" Format="yyyy-MM-dd HH:mm:ss" />
                            <TemplateColumn Title="Error" SortBy="@errorSort">
                                <ColumnOptions>
                                    <FluentSearch @bind-Value="activityErrorFilter"
                                                  @bind-Value:after="ApplyActivityFilters"
                                                  Placeholder="Filter..." />
                                </ColumnOptions>
                                <ChildContent>
                                    @if (context.ErrorState != null)
                                    {
                                        <span>[@context.ErrorState.Code] @context.ErrorState.Message</span>
                                    }
                                    else
                                    {
                                        <span>-</span>
                                    }
                                </ChildContent>
                            </TemplateColumn>
                        </FluentDataGrid>
                    </FluentTab>

                    <FluentTab Label="Variables">
                        @if (snapshot!.VariableStates.Count == 0)
                        {
                            <FluentMessageBar Intent="MessageIntent.Info">
                                No variable states recorded.
                            </FluentMessageBar>
                        }
                        else
                        {
                            @foreach (var vs in snapshot.VariableStates)
                            {
                                var rows = GetVariableRows(vs);
                                <FluentLabel Typo="Typography.Subject" Style="margin-top: 12px;">
                                    Variables @vs.VariablesId.ToString()[..8]...
                                </FluentLabel>
                                <FluentDataGrid Items="@rows" GridTemplateColumns="1fr 2fr">
                                    <PropertyColumn Property="@(v => v.Key)" Title="Key" Sortable="true" />
                                    <PropertyColumn Property="@(v => v.Value)" Title="Value" Sortable="true" />
                                </FluentDataGrid>
                            }
                        }
                    </FluentTab>

                    <FluentTab Label="Conditions">
                        @if (snapshot!.ConditionSequences.Count == 0)
                        {
                            <FluentMessageBar Intent="MessageIntent.Info">
                                No condition evaluations recorded.
                            </FluentMessageBar>
                        }
                        else
                        {
                            <FluentDataGrid Items="@filteredConditions" GridTemplateColumns="1fr 2fr 1.5fr 0.5fr">
                                <PropertyColumn Property="@(c => c.SequenceFlowId)" Title="Sequence Flow" Sortable="true">
                                    <ColumnOptions>
                                        <FluentSearch @bind-Value="conditionFlowFilter"
                                                      @bind-Value:after="ApplyConditionFilters"
                                                      Placeholder="Filter..." />
                                    </ColumnOptions>
                                </PropertyColumn>
                                <PropertyColumn Property="@(c => c.Condition)" Title="Condition" Sortable="true">
                                    <ColumnOptions>
                                        <FluentSearch @bind-Value="conditionExprFilter"
                                                      @bind-Value:after="ApplyConditionFilters"
                                                      Placeholder="Filter..." />
                                    </ColumnOptions>
                                </PropertyColumn>
                                <TemplateColumn Title="Path" SortBy="@conditionPathSort">
                                    <ColumnOptions>
                                        <FluentSearch @bind-Value="conditionPathFilter"
                                                      @bind-Value:after="ApplyConditionFilters"
                                                      Placeholder="Filter..." />
                                    </ColumnOptions>
                                    <ChildContent>
                                        <span>@context.SourceActivityId → @context.TargetActivityId</span>
                                    </ChildContent>
                                </TemplateColumn>
                                <TemplateColumn Title="Result" SortBy="@conditionResultSort">
                                    <ColumnOptions>
                                        <FluentSelect TOption="string" @bind-Value="conditionResultFilter"
                                                      @bind-Value:after="ApplyConditionFilters">
                                            <FluentOption Value="">All</FluentOption>
                                            <FluentOption Value="true">True</FluentOption>
                                            <FluentOption Value="false">False</FluentOption>
                                        </FluentSelect>
                                    </ColumnOptions>
                                    <ChildContent>
                                        @if (context.Result)
                                        {
                                            <FluentBadge Color="Color.Success">True</FluentBadge>
                                        }
                                        else
                                        {
                                            <FluentBadge Color="Color.Error">False</FluentBadge>
                                        }
                                    </ChildContent>
                                </TemplateColumn>
                            </FluentDataGrid>
                        }
                    </FluentTab>
                </FluentTabs>
            </div>
        </div>
    }
```

**Step 2: Add canvas height variable and drag callback**

In the `@code` block, add a field for canvas height:

```csharp
private int canvasHeight = 400;
```

Add a JSInvokable callback for when the drag ends:

```csharp
[JSInvokable]
public async Task OnCanvasResized(double newHeight)
{
    canvasHeight = (int)newHeight;
    StateHasChanged();
    // Refit the BPMN diagram to the new canvas size
    if (bpmnXml != null)
    {
        await JS.InvokeVoidAsync("bpmnViewer.refit");
    }
}
```

**Step 3: Initialize the vertical drag handle after rendering**

Update `RenderDiagram` to also initialize the drag handle:

```csharp
private async Task RenderDiagram()
{
    if (bpmnXml == null || snapshot == null) return;

    await JS.InvokeVoidAsync("bpmnViewer.init", "bpmn-canvas", bpmnXml);
    await JS.InvokeVoidAsync("bpmnViewer.highlight", snapshot.CompletedActivityIds, snapshot.ActiveActivityIds);

    dotNetRef = DotNetObjectReference.Create(this);
    await JS.InvokeVoidAsync("bpmnViewer.registerClickHandler", dotNetRef);

    // Initialize vertical drag handle
    var maxHeight = 800; // reasonable max
    await JS.InvokeVoidAsync("dragResize.initVertical", "vertical-drag-handle", "bpmn-canvas", 200, maxHeight, dotNetRef, "OnCanvasResized");
}
```

**Step 4: Add bpmnViewer.refit function**

In `bpmnViewer.js`, add a `refit` function after `selectElement`:

```javascript
refit: function () {
    if (!this._viewer) return;
    var canvas = this._viewer.get('canvas');
    canvas.zoom('fit-viewport');
},
```

**Step 5: Add CSS for instance layout**

Append to `app.css`:

```css
/* Instance Detail Layout */
.instance-content {
    display: flex;
    flex: 1;
    min-height: 0;
}

.instance-main {
    display: flex;
    flex-direction: column;
    flex: 1;
    min-width: 0;
    gap: 8px;
}
```

**Step 6: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 7: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor src/Fleans/Fleans.Web/wwwroot/js/bpmnViewer.js src/Fleans/Fleans.Web/wwwroot/app.css
git commit -m "feat: vertically resizable BPMN viewer on instance detail page"
```

---

### Task 7: Read-Only Properties Panel with Horizontal Resize

Add a read-only properties panel to the ProcessInstance page with horizontal drag-resize.

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/ElementPropertiesPanel.razor`
- Modify: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor`
- Modify: `src/Fleans/Fleans.Web/wwwroot/js/bpmnViewer.js`

**Step 1: Add ReadOnly parameter to ElementPropertiesPanel.razor**

Add a `ReadOnly` parameter and conditionally disable fields. Add after the existing parameters:

```csharp
[Parameter] public bool ReadOnly { get; set; }
```

Wrap the type selector in a ReadOnly check. Replace the type selector block (lines 11-22):

```razor
@if (!ReadOnly && GetReplaceOptions() is { Count: > 1 } options)
{
    <div>
        <FluentLabel Typo="Typography.Body" Weight="FontWeight.Bold">Type</FluentLabel>
        <FluentSelect TOption="string" Value="@Element.Type" @onchange="OnTypeChange" Style="width: 100%;">
            @foreach (var opt in options)
            {
                <FluentOption TOption="string" Value="@opt.Key" Selected="@(opt.Key == Element.Type)">@opt.Value</FluentOption>
            }
        </FluentSelect>
    </div>
}
```

Add `Disabled="@ReadOnly"` to all text fields:

- The ID `FluentTextField`: add `Disabled="@ReadOnly"`
- The Name `FluentTextField`: add `Disabled="@ReadOnly"`
- The Script Format `FluentTextField`: add `Disabled="@ReadOnly"`
- The Script `FluentTextArea`: add `Disabled="@ReadOnly"`
- The Condition Expression `FluentTextArea`: add `Disabled="@ReadOnly"`

**Step 2: Add getElementProperties to bpmnViewer.js**

Add after the `refit` function (added in Task 6):

```javascript
getElementProperties: function (elementId) {
    if (!this._viewer) return null;

    var elementRegistry = this._viewer.get('elementRegistry');
    var element = elementRegistry.get(elementId);
    if (!element) return null;

    var bo = element.businessObject;
    return {
        id: bo.id || '',
        type: element.type || '',
        name: bo.name || '',
        scriptFormat: bo.scriptFormat || '',
        script: bo.script || '',
        conditionExpression: (bo.conditionExpression && bo.conditionExpression.body) || ''
    };
},
```

**Step 3: Update ProcessInstance.razor to show properties panel**

In the markup, add the horizontal drag handle and properties panel inside `instance-content`, after `instance-main`. The layout becomes:

```razor
<div class="instance-content">
    <div class="instance-main">
        @* ... existing canvas, drag handle, tabs ... *@
    </div>

    @if (selectedElementData != null)
    {
        <div id="horizontal-drag-handle" class="drag-handle-horizontal"></div>
        <div id="instance-properties-panel">
            <ElementPropertiesPanel Element="@selectedElementData" ReadOnly="true" />
        </div>
    }
</div>
```

Add a field for the selected element data in the `@code` block:

```csharp
private ElementPropertiesPanel.BpmnElementData? selectedElementData;
```

Update `OnBpmnElementClicked` to fetch element properties:

```csharp
[JSInvokable]
public async Task OnBpmnElementClicked(string elementId)
{
    selectedActivityId = string.IsNullOrEmpty(elementId) ? null : elementId;

    if (!string.IsNullOrEmpty(elementId))
    {
        var props = await JS.InvokeAsync<ElementPropertiesPanel.BpmnElementData?>("bpmnViewer.getElementProperties", elementId);
        selectedElementData = props;
    }
    else
    {
        selectedElementData = null;
    }

    await InvokeAsync(StateHasChanged);
}
```

Note: The `OnBpmnElementClicked` method signature changes from `void` to `async Task`. It's still `[JSInvokable]` — .NET interop supports async returns.

Update `OnActivityRowClick` to also fetch element properties:

```csharp
private async Task OnActivityRowClick(FluentDataGridRow<ActivityInstanceSnapshot> row)
{
    if (row.Item is null) return;
    selectedActivityId = row.Item.ActivityId;
    await JS.InvokeVoidAsync("bpmnViewer.selectElement", row.Item.ActivityId);

    var props = await JS.InvokeAsync<ElementPropertiesPanel.BpmnElementData?>("bpmnViewer.getElementProperties", row.Item.ActivityId);
    selectedElementData = props;
}
```

Initialize the horizontal drag handle. Add to `RenderDiagram`, after the vertical drag handle init — but this should be conditional on `selectedElementData`. Better: initialize it after element selection. Add a helper method:

```csharp
private async Task InitHorizontalResize()
{
    if (dotNetRef == null) return;
    var maxWidth = 600;
    await JS.InvokeVoidAsync("dragResize.initHorizontal", "horizontal-drag-handle", "instance-properties-panel", 200, maxWidth, dotNetRef, "OnPanelResized");
}
```

Call `InitHorizontalResize()` at the end of `OnBpmnElementClicked` (inside the non-empty branch, after StateHasChanged):

```csharp
if (!string.IsNullOrEmpty(elementId))
{
    // ... fetch props ...
    await InvokeAsync(StateHasChanged);
    await Task.Yield(); // let Blazor render the panel DOM
    await InitHorizontalResize();
}
```

Add the callback:

```csharp
[JSInvokable]
public void OnPanelResized(double newWidth)
{
    // Panel width is managed by JS directly on the DOM element
}
```

**Step 4: Add CSS for instance properties panel**

Append to `app.css`:

```css
#instance-properties-panel {
    width: 300px;
    flex-shrink: 0;
    overflow: hidden;
}
```

**Step 5: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/ElementPropertiesPanel.razor src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor src/Fleans/Fleans.Web/wwwroot/js/bpmnViewer.js src/Fleans/Fleans.Web/wwwroot/app.css
git commit -m "feat: read-only properties panel with horizontal resize on instance detail"
```

---

### Task 8: Final Verification

**Step 1: Build**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds

**Step 2: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 3: Commit if any remaining changes**

If there are any uncommitted changes, stage and commit them.
