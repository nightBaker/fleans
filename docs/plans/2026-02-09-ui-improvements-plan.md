# UI Improvements Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Five UI improvements: compact FluentAppBar navigation, vertically resizable BPMN viewer, read-only properties panel on instance detail page, edit any version of BPMN, and version-filtered instances.

**Architecture:** Replace the 250px FluentNavMenu with a ~48px FluentAppBar. Add drag-handle resize (vertical for BPMN canvas, horizontal for properties panel) via a shared JS utility with listener deduplication. Add `ReadOnly` parameter to `ElementPropertiesPanel`. Add two new backend methods (`GetBpmnXmlByKeyAndVersion`, `GetInstancesByKeyAndVersion`) and wire them through to the UI with new route parameters.

**Tech Stack:** .NET 10, Blazor Server, Fluent UI Blazor, bpmn.js (navigated-viewer), Orleans 9.2.1, JavaScript interop

**Status:** COMPLETED. All tasks implemented and verified. Code review findings addressed.

---

### Task 1: FluentAppBar Navigation [DONE]

Replace the 250px `FluentNavMenu` sidebar with a compact vertical `FluentAppBar`.

**Files:**
- Modified: `src/Fleans/Fleans.Web/Components/Layout/MainLayout.razor`
- Modified: `src/Fleans/Fleans.Web/Components/Layout/NavMenu.razor` (emptied)
- Modified: `src/Fleans/Fleans.Web/Components/Layout/NavMenu.razor.css` (dead CSS removed)
- Modified: `src/Fleans/Fleans.Web/Components/Layout/MainLayout.razor.css` (dead CSS removed, kept `#blazor-error-ui` only)

**What was done:**

Replaced the `FluentNavMenu Width="250"` block in MainLayout with a `FluentAppBar`:

```razor
<FluentAppBar Style="height: calc(100vh - 50px);">
    <FluentAppBarItem Href="/workflows"
                      Text="Workflows"
                      IconRest="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size24.Flow())" />
    <FluentAppBarItem Href="/editor"
                      Text="Editor"
                      IconRest="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size24.DrawShape())" />
</FluentAppBar>
```

NavMenu.razor emptied to `@* Navigation moved to FluentAppBar in MainLayout *@`.

Dead CSS from the Blazor template was cleaned up in both `NavMenu.razor.css` (106 lines of `.navbar-toggler`, `.nav-item`, etc.) and `MainLayout.razor.css` (`.page`, `.sidebar`, `.top-row` — kept only `#blazor-error-ui`).

---

### Task 2: Backend — GetBpmnXmlByKeyAndVersion & GetInstancesByKeyAndVersion [DONE]

Add two new grain methods and wire them through `WorkflowEngine`.

**Files:**
- Modified: `src/Fleans/Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs`
- Modified: `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs`
- Modified: `src/Fleans/Fleans.Application/WorkflowEngine.cs`

**What was done:**

Added two interface methods:

```csharp
Task<string> GetBpmnXmlByKeyAndVersion(string processDefinitionKey, int version);
Task<IReadOnlyList<WorkflowInstanceInfo>> GetInstancesByKeyAndVersion(string processDefinitionKey, int version);
```

Grain implementation looks up `_byKey[key]` for the matching version, throws `KeyNotFoundException` if not found. `GetInstancesByKeyAndVersion` filters `_instancesByKey` by matching `_instanceToDefinitionId` to the target version's `ProcessDefinitionId`.

WorkflowEngine facade methods delegate to the factory grain.

---

### Task 3: Edit Any Version of BPMN [DONE]

Add a versioned route to the Editor page and update the Workflows page to link to it.

**Files:**
- Modified: `src/Fleans/Fleans.Web/Components/Pages/Editor.razor`
- Modified: `src/Fleans/Fleans.Web/Components/Pages/Workflows.razor`

**What was done:**

Added route `@page "/editor/{ProcessDefinitionKey}/{Version:int}"` and `[Parameter] public int? Version { get; set; }`.

Version-aware BPMN loading: uses `GetBpmnXmlByKeyAndVersion` when `Version.HasValue`, otherwise `GetBpmnXmlByKey`.

Toolbar badge shows `"Editing v{Version} of {processKey}"` when version specified.

`EditWorkflow` in Workflows.razor navigates to `/editor/{Key}/{Version}`.

---

### Task 4: Instances Filtered by Version [DONE]

Add a versioned route to the ProcessInstances page and update the Workflows page to link to it.

**Files:**
- Modified: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstances.razor`
- Modified: `src/Fleans/Fleans.Web/Components/Pages/Workflows.razor`

**What was done:**

Added route `@page "/process-instances/{ProcessDefinitionKey}/{Version:int}"` and `[Parameter] public int? Version { get; set; }`.

`GetPageTitle()` returns `"Instances -- {Key} v{Version}"` or `"Instances -- {Key} (all versions)"`.

`LoadInstances` calls `GetInstancesByKeyAndVersion` when `Version.HasValue`, otherwise `GetInstancesByKey`.

`ViewInstances` in Workflows.razor navigates to `/process-instances/{Key}/{Version}`.

---

### Task 5: Drag Handle Resize JS Utility [DONE]

Create a small JS utility for drag-handle resizing, used by both vertical (BPMN canvas) and horizontal (properties panel) resizers.

**Files:**
- Created: `src/Fleans/Fleans.Web/wwwroot/js/dragResize.js`
- Modified: `src/Fleans/Fleans.Web/Components/App.razor` (added script reference)
- Modified: `src/Fleans/Fleans.Web/wwwroot/app.css` (added drag handle CSS)

**What was done:**

Created `window.dragResize` with three functions:

- `getViewportHeight()` — returns `window.innerHeight` for dynamic max height computation
- `initVertical(handleId, topId, minHeight, maxHeight, dotNetRef, callbackMethod)` — vertical drag resize with listener deduplication
- `initHorizontal(handleId, panelId, minWidth, maxWidth, dotNetRef, callbackMethod)` — horizontal drag resize with listener deduplication

Both `initVertical` and `initHorizontal` deduplicate event listeners to prevent accumulation on repeated calls:

```javascript
if (handle._dragResizeHandler) {
    handle.removeEventListener('mousedown', handle._dragResizeHandler);
}
handle._dragResizeHandler = onMouseDown;
handle.addEventListener('mousedown', onMouseDown);
```

CSS classes `.drag-handle-vertical` and `.drag-handle-horizontal` added with pill-shaped indicator pseudo-elements.

Script reference added to `App.razor` after `bpmnEditor.js`.

---

### Task 6: Vertically Resizable BPMN Viewer [DONE]

Replace the fixed-height `.bpmn-container` on the ProcessInstance page with a drag-handle resizable layout.

**Files:**
- Modified: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor`
- Modified: `src/Fleans/Fleans.Web/wwwroot/js/bpmnViewer.js`
- Modified: `src/Fleans/Fleans.Web/wwwroot/app.css`

**What was done:**

Canvas height bound to `canvasHeight` field (default 400px) via inline style `style="height: @(canvasHeight)px;"`.

Drag handle `<div id="vertical-drag-handle" class="drag-handle-vertical"></div>` placed below the canvas.

`RenderDiagram()` initializes the vertical drag handle with dynamic max height computed from viewport:

```csharp
var viewportHeight = await JS.InvokeAsync<int>("dragResize.getViewportHeight");
var maxHeight = Math.Max(viewportHeight - 200, 400);
await JS.InvokeVoidAsync("dragResize.initVertical", "vertical-drag-handle", "bpmn-canvas", 200, maxHeight, dotNetRef, "OnCanvasResized");
```

`OnCanvasResized` JSInvokable callback updates `canvasHeight` and calls `bpmnViewer.refit` to re-zoom the diagram.

Added `bpmnViewer.refit()` function that calls `canvas.zoom('fit-viewport')`.

Removed fixed `height` from `.bpmn-container` CSS. Added `.instance-content` (flex row) and `.instance-main` (flex column) layout classes.

---

### Task 7: Read-Only Properties Panel with Horizontal Resize [DONE]

Add a read-only properties panel to the ProcessInstance page with horizontal drag-resize.

**Files:**
- Modified: `src/Fleans/Fleans.Web/Components/Pages/ElementPropertiesPanel.razor`
- Modified: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor`
- Modified: `src/Fleans/Fleans.Web/wwwroot/js/bpmnViewer.js`
- Modified: `src/Fleans/Fleans.Web/wwwroot/app.css`

**What was done:**

Added `[Parameter] public bool ReadOnly { get; set; }` to `ElementPropertiesPanel.razor`. Type selector hidden when `ReadOnly`. All `FluentTextField` and `FluentTextArea` components get `Disabled="@ReadOnly"`.

Added `bpmnViewer.getElementProperties(elementId)` JS function that reads element `businessObject` properties (id, type, name, scriptFormat, script, conditionExpression).

Added `bpmn:SequenceFlow` to the `_activityTypes` array in `bpmnViewer.js` so sequence flows are clickable and show properties.

`OnBpmnElementClicked` changed from `void` to `async Task` — fetches element properties via JS interop, shows properties panel, initializes horizontal resize after Blazor renders the panel DOM.

`OnActivityRowClick` also fetches element properties and initializes horizontal resize.

Properties panel markup:

```razor
@if (selectedElementData != null)
{
    <div id="horizontal-drag-handle" class="drag-handle-horizontal"></div>
    <div id="instance-properties-panel">
        <ElementPropertiesPanel Element="@selectedElementData" ReadOnly="true" />
    </div>
}
```

`#instance-properties-panel` CSS uses `overflow-y: auto` (not `overflow: hidden`) so long content is scrollable.

`OnPanelResized` is intentionally empty — panel width managed by JS on the DOM element directly.

---

### Task 8: Final Verification [DONE]

Build: 0 errors, 0 warnings.
Tests: 142/142 passing (92 Domain + 12 Application + 38 Infrastructure).

---

### Code Review Fixes [DONE]

After initial implementation, a code review identified the following issues, all addressed:

1. **Event listener accumulation** — `dragResize.js` now removes old `mousedown` handler before attaching new one
2. **Dead CSS cleanup** — `NavMenu.razor.css` (106 lines) and `MainLayout.razor.css` (dead `.page`, `.sidebar`, `.top-row`) cleaned up
3. **Design doc contradiction** — Removed `WorkflowInstanceInfo.Version` from in-scope, moved to out-of-scope
4. **Dynamic max canvas height** — Computed from `viewportHeight - 200` via `dragResize.getViewportHeight()` instead of hardcoded 800px
5. **Properties panel overflow** — Changed from `overflow: hidden` to `overflow-y: auto`
6. **SequenceFlow clickable** — Added `bpmn:SequenceFlow` to `_activityTypes` in `bpmnViewer.js`
7. **Empty callback comment** — Added explanatory comment to `OnPanelResized`

**Not fixed (accepted):** `[LoggerMessage]` source generators in Razor pages — pre-existing pattern across all Razor components; would require `.razor.cs` code-behind files.
