# Editable Process ID on BPMN Canvas

## Problem

When creating or editing a BPMN diagram, the process ID defaults to `Process_1` and cannot be changed in the editor UI. Users must manually edit the BPMN XML to set a meaningful process ID before deployment.

## Solution

Add a fixed-position, click-to-edit text element in the top-left corner of the BPMN canvas that displays and allows editing the `<process id>` attribute. Changes update the BPMN XML in-place via bpmn-js `modeling.updateProperties()`.

## Components Changed

### `bpmnEditor.js`

- `getProcessId()`: Returns the current root process element's `id`.
- `updateProcessId(newId)`: Updates the root process element's `id` via `modeling.updateProperties()`.
- `init()` and `loadXml()`: Call back to Blazor with the initial process ID after loading.

### `Editor.razor`

- A `<div>` positioned absolutely over the top-left of the canvas.
- Displays the process ID as text by default.
- On click, switches to a `<FluentTextField>` for inline editing.
- On Enter/blur, calls `bpmnEditor.updateProcessId(newId)` and switches back to display mode.
- Validates: non-empty, no spaces (BPMN ID constraints).

### `app.css`

- Fixed in top-left of `.editor-canvas` with slight padding.
- Semi-transparent background so it doesn't obscure the diagram.
- Subtle styling to indicate it's editable (underline on hover).

## Data Flow

```
User clicks title → FluentTextField appears → User types new ID → Enter/blur →
JS: modeling.updateProperties(processElement, { id: newId }) →
Blazor: processKey updated → UI refreshes title display
```

## Validation

- Non-empty.
- No spaces — BPMN `id` is an XML ID type (alphanumeric, underscores, hyphens).
- Inline error shown if invalid.

## What Stays the Same

- No domain model changes (`ProcessDefinition`, `WorkflowDefinition` untouched).
- No backend changes — the process ID already flows from BPMN XML through `BpmnConverter` to `ProcessDefinitionKey`.
- Deploy flow continues to extract the process ID from XML.
