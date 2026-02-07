# BPMN Editor Page Design

**Date:** 2026-02-07
**Updated:** 2026-02-07 (post-implementation learnings)

## Goal

Add a dedicated BPMN editor page to the Blazor admin UI using bpmn-js Modeler (CDN, without official properties panel). Users can create new BPMN diagrams from scratch, edit existing definitions, import BPMN files, edit element properties via a custom Blazor side panel, change element types, and deploy — all from one page. The existing file upload panel on the Workflows page is removed in favor of this centralized editor.

## Page & Navigation

**Routes:** `/editor` (blank canvas) and `/editor/{ProcessDefinitionKey}` (load latest version of existing definition).

**Navigation entry points:**

- **Nav menu:** "Editor" link in `NavMenu.razor` pointing to `/editor`
- **Workflows datagrid:** "Edit" button on each row navigating to `/editor/{processDefinitionKey}`
- **Workflows page:** "New Workflow" button navigating to `/editor`

**Page layout:**

- Full-width page — bpmn-js modeler needs maximum canvas space
- Top toolbar: **Back** (to Workflows) | process key label (if editing existing) | **Import BPMN** | **Download XML** | **Deploy**
- Flex row below toolbar: modeler canvas (flex: 1) + properties panel (300px, right side, conditionally rendered)
- Properties panel appears when an element is selected, hides when deselected — canvas resizes automatically

## CDN Setup & Global Variable Disambiguation

**CDN dependencies added to `App.razor`:** bpmn-js v17.11.1

Both the navigated viewer and modeler CDN bundles export the same global variable `BpmnJS`. Loading both on the same page causes the second to overwrite the first. **Fix:** After each CDN script loads, immediately save the constructor to a unique global variable:

```html
<script src="https://unpkg.com/bpmn-js@17.11.1/dist/bpmn-navigated-viewer.production.min.js"></script>
<script>window.BpmnViewer = BpmnJS;</script>
<script src="js/bpmnViewer.js"></script>

<script src="https://unpkg.com/bpmn-js@17.11.1/dist/bpmn-modeler.production.min.js"></script>
<script>window.BpmnModeler = BpmnJS;</script>
<script src="js/bpmnEditor.js"></script>
```

CSS files: `diagram-js.css`, `bpmn-js.css`, `bpmn-font/css/bpmn.css` (all from unpkg CDN).

**Consequence:** `bpmnViewer.js` uses `new BpmnViewer(...)` and `bpmnEditor.js` uses `new BpmnModeler(...)`.

## JavaScript Interop

File `wwwroot/js/bpmnEditor.js` exposing `window.bpmnEditor`:

- **`init(containerId, dotNetRef)`** — creates BpmnModeler instance with `keyboard: { bindTo: container }` (see Keyboard section). Accepts a Blazor `DotNetObjectReference`. Registers a `selection.changed` event listener: on single element select calls `dotNetRef.invokeMethodAsync('OnElementSelected', elementData)`, on deselect calls `dotNetRef.invokeMethodAsync('OnElementDeselected')`
- **`_extractElementData(element)`** — reads id, type, name from `businessObject`, plus type-specific fields (scriptFormat/script for ScriptTask, conditionExpression body for SequenceFlow)
- **`loadXml(bpmnXml)`** — imports BPMN XML into the modeler, zooms to fit viewport
- **`newDiagram()`** — loads a minimal blank BPMN diagram (empty process with one start event)
- **`getXml()`** — exports current canvas as BPMN XML string (formatted), returned to Blazor
- **`getElementProperties(elementId)`** — returns current properties of an element from the modeler's internal model
- **`updateElementProperty(elementId, propertyName, value)`** — updates a single property on an element using `modeling.updateProperties`. Special handling for `conditionExpression`: creates a `bpmn:FormalExpression` via `moddle.create()` when value is non-empty, sets to `undefined` when empty
- **`updateElementId(oldId, newId)`** — updates element ID using `modeling.updateProperties`, verifies the element exists under the new ID in the registry, returns updated element data. This is a dedicated method because ID changes require re-lookup in the element registry
- **`replaceElement(elementId, newType)`** — replaces an element with a different type using `bpmnReplace.replaceElement()`, re-selects the new element, returns updated element data
- **`destroy()`** — cleanup on page leave

Element data object passed to Blazor:
```json
{
  "id": "Task_1",
  "type": "bpmn:ScriptTask",
  "name": "Run script",
  "scriptFormat": "javascript",
  "script": "console.log('hello')",
  "conditionExpression": ""
}
```

### Keyboard Binding

bpmn-js registers keyboard shortcuts (e.g. 's' for lasso tool, 'e' for direct editing). When bound to `document`, these intercept keystrokes in form inputs throughout the page. **Fix:** bind keyboard to the canvas container element only:

```javascript
this._modeler = new BpmnModeler({
    container: container,
    keyboard: { bindTo: container }
});
```

This way, bpmn-js shortcuts only fire when the canvas has focus, and typing in properties panel inputs works normally.

## Custom Properties Panel

**File: `Components/Pages/ElementPropertiesPanel.razor`**

A Blazor component rendered inside `Editor.razor` that provides element property editing without the official bpmn-js-properties-panel (which requires npm + bundler and is NOT available as a standalone CDN bundle).

**Data flow:**
1. User clicks element in modeler canvas
2. JS fires `OnElementSelected` → Blazor receives element data
3. Panel renders editable fields based on element type
4. User edits a field → on change, calls JS interop to update the modeler
5. For type/ID changes, the `OnElementReplaced` callback syncs the new element data back to `Editor.razor`
6. User clicks canvas background → JS fires `OnElementDeselected` → panel hides

**Editable properties per element type:**

| Element Type | Properties |
|---|---|
| All elements | ID (editable), Name |
| Task / User Task / Service Task | ID, Name |
| Script Task | ID, Name, Script format, Script body (textarea) |
| Exclusive Gateway / Parallel Gateway | ID, Name |
| Sequence Flow | ID, Name, Condition expression |
| Start Event / End Event / Intermediate Events | ID, Name |

**Element type dropdown:**

When the selected element belongs to a type group, a "Type" dropdown appears allowing in-place replacement:

| Group | Options |
|---|---|
| Tasks | Task, User Task, Service Task, Script Task |
| Gateways | Exclusive Gateway, Parallel Gateway |
| Events | Start Event, End Event, Intermediate Throw Event, Intermediate Catch Event |

Type changes use `bpmnEditor.replaceElement(elementId, newType)` which calls `bpmnReplace.replaceElement()` internally. The replacement creates a new element (new shape, new ID), so the `OnElementReplaced` callback updates Blazor state.

**ID editing:**

Element IDs are editable per [Camunda best practices](https://docs.camunda.io/docs/components/best-practices/modeling/naming-technically-relevant-ids/). Uses a dedicated `bpmnEditor.updateElementId(oldId, newId)` JS method. After the ID change, the method verifies the element exists under the new ID in the element registry and returns the updated element data via `OnElementReplaced`.

**Panel layout:**
- Fixed-width (300px) panel on the right side, full height of editor area
- Header showing element type label
- Form fields update the modeler on blur/change, not on every keystroke
- Uses `@oninput` for local state + `@onchange` for JS interop calls

**FluentUI component notes:**
- `FluentSelect` and `FluentOption` require `TOption="string"` generic parameter
- `FluentTextField` for single-line inputs, `FluentTextArea` for multi-line (script, condition expression)

## Deploy Dialog

Uses a plain HTML overlay + dialog rather than `FluentOverlay` or `FluentDialog`. The FluentUI dialog components had issues: `FluentDialog` doesn't expose `ShowAsync`/`HideAsync` methods, and `FluentOverlay` was firing `OnClose` immediately, causing the deploy button to appear to "do nothing".

**Implementation:** A boolean `showDeployDialog` controls conditional rendering of `<div class="deploy-overlay">` (backdrop) and `<div class="deploy-dialog">` (centered card). Clicking the overlay backdrop cancels the dialog.

**Deploy flow:**
1. Click Deploy → export XML → parse with `BpmnConverter.ConvertFromXmlAsync()` → compute next version number → show dialog
2. Dialog shows process key, next version number, confirm/cancel buttons
3. Confirm → `WorkflowEngine.DeployWorkflow(workflow, bpmnXml)` → success message bar
4. Cancel → hide dialog, clear pending state

## Editor Razor Component

File `Components/Pages/Editor.razor`.

**Lifecycle:**

1. `OnAfterRenderAsync` (first render) — create `DotNetObjectReference<Editor>`, call `bpmnEditor.init("editor-canvas", dotNetRef)`
2. If `ProcessDefinitionKey` is set — fetch BPMN XML via `WorkflowEngine.GetBpmnXmlByKey(key)`, call `bpmnEditor.loadXml(xml)`
3. If no key — call `bpmnEditor.newDiagram()`
4. `DisposeAsync` — call `bpmnEditor.destroy()`, dispose `DotNetObjectReference`

**Toolbar actions:**

- **Import BPMN** — `FluentInputFile` reads a `.bpmn/.xml` file, calls `bpmnEditor.loadXml(xml)` to load into canvas
- **Download XML** — calls `bpmnEditor.getXml()`, triggers browser file download via base64 data URL
- **Deploy** — calls `bpmnEditor.getXml()`, parses with `BpmnConverter`, shows confirmation dialog, on confirm calls `WorkflowEngine.DeployWorkflow()`

**Element selection callbacks (JSInvokable):**

- `OnElementSelected(BpmnElementData elementData)` — stores selected element, triggers StateHasChanged via InvokeAsync
- `OnElementDeselected()` — clears selected element, hides panel
- `OnElementReplaced(BpmnElementData newElement)` — updates selected element after type change or ID change

**FluentUI icon notes:**

Use full namespace for icons: `new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.ArrowLeft()`. The shorthand `new Icons.Regular.Size20.ArrowLeft()` causes CS0246 without an explicit `using` for the nested `Icons` class.

## Backend Changes

One new method through the grain chain:

- `WorkflowEngine.GetBpmnXmlByKey(string processDefinitionKey)` — returns BPMN XML of the latest version for a key
- `IWorkflowInstanceFactoryGrain.GetBpmnXmlByKey(string key)` — interface addition
- `WorkflowInstanceFactoryGrain.GetBpmnXmlByKey(string key)` — looks up latest version from `_byKey` dictionary, returns `BpmnXml`

## File Changes

**New files:**

- `Fleans.Web/wwwroot/js/bpmnEditor.js`
- `Fleans.Web/Components/Pages/Editor.razor`
- `Fleans.Web/Components/Pages/ElementPropertiesPanel.razor`

**Modified files:**

- `Fleans.Web/Components/App.razor` — add modeler CDN scripts/CSS with global variable disambiguation
- `Fleans.Web/Components/Layout/NavMenu.razor` — add "Editor" nav link
- `Fleans.Web/Components/Pages/Workflows.razor` — remove upload panel, add "New Workflow" button, add "Edit" action to datagrid rows
- `Fleans.Web/wwwroot/app.css` — editor layout, properties panel, and deploy dialog styles
- `Fleans.Web/wwwroot/js/bpmnViewer.js` — changed `new BpmnJS(...)` to `new BpmnViewer(...)` for CDN global variable fix
- `Fleans.Application/WorkflowEngine.cs` — add `GetBpmnXmlByKey`
- `Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs` — add `GetBpmnXmlByKey`
- `Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs` — implement `GetBpmnXmlByKey`

**Deleted files:**

- `Fleans.Web/Components/Pages/WorkflowUploadPanel.razor`

**Not changed:**

- API endpoints — no changes (Web UI talks directly to grains)
- Domain models — no changes

## Implementation Gotchas

Lessons learned during implementation:

1. **CDN global variable conflict** — Both bpmn-js viewer and modeler CDN bundles export `BpmnJS`. Must save each to a unique global (`BpmnViewer`/`BpmnModeler`) immediately after the script loads.

2. **Keyboard shortcut interference** — bpmn-js keyboard shortcuts intercept form input when bound to `document`. Bind to the canvas container element instead.

3. **FluentOverlay unreliable for dialogs** — `FluentOverlay.OnClose` fires immediately in some cases, making the dialog appear to do nothing. Use plain HTML overlay + dialog with boolean toggle instead.

4. **FluentDialog lacks Show/Hide API** — `FluentDialog` in FluentUI Blazor v4.13.2 doesn't have `ShowAsync`/`HideAsync` methods.

5. **FluentSelect needs TOption** — `FluentSelect` and `FluentOption` require explicit `TOption="string"` generic parameter or they fail to compile.

6. **Icons require full namespace** — `new Icons.Regular.Size20.Add()` causes CS0246. Use `new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.Add()`.

7. **Element ID changes** — `modeling.updateProperties(element, { id: newId })` works but the element must be re-looked-up in the registry under the new ID. A dedicated method handles this safely.

8. **Element type replacement** — Use `bpmnReplace.replaceElement()` from the modeler's `bpmnReplace` module. The replacement creates a new element instance, so Blazor state must be updated via callback.

9. **Condition expressions** — Setting `conditionExpression` requires creating a `bpmn:FormalExpression` object via `moddle.create()`, not just passing a string.
