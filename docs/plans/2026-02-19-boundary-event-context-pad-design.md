# Boundary Event Context Pad for BPMN Editor

**Date:** 2026-02-19
**Status:** Approved

## Problem

The BPMN editor has no UI mechanism to create boundary events. The bpmn-js default context pad on tasks only shows "Append" actions (sequence flow targets), not "Attach boundary event" actions. Users cannot add boundary events without editing XML directly.

## Solution

Custom bpmn-js `ContextPadProvider` that adds boundary event attachment icons to the context pad of boundarable activities (tasks and call activities).

## Architecture

A single new JS file (`boundaryEventContextPad.js`) defines the provider module. It registers via `additionalModules` when the modeler is created in `bpmnEditor.js`.

### Files Changed

1. **New:** `wwwroot/js/boundaryEventContextPad.js` — custom context pad provider module
2. **Modified:** `wwwroot/js/bpmnEditor.js` — pass module in `additionalModules`
3. **Modified:** `Editor.razor` or layout — add `<script>` tag for new JS file

No Blazor/C# changes needed. Existing `_extractElementData()`, `ElementPropertiesPanel`, and `BpmnConverter` already handle boundary events.

## Context Pad Entries

The provider activates on boundarable element types: `bpmn:Task`, `bpmn:UserTask`, `bpmn:ServiceTask`, `bpmn:ScriptTask`, `bpmn:CallActivity`, `bpmn:SubProcess`.

Three new entries in an `"attach"` group:

| Entry | Icon Class | Event Definition |
|-------|-----------|-----------------|
| Attach Timer Boundary | `bpmn-icon-intermediate-event-catch-timer` | `bpmn:TimerEventDefinition` |
| Attach Message Boundary | `bpmn-icon-intermediate-event-catch-message` | `bpmn:MessageEventDefinition` |
| Attach Error Boundary | `bpmn-icon-intermediate-event-catch-error` | `bpmn:ErrorEventDefinition` |

## Boundary Event Creation Flow

1. User clicks a boundary event icon on the context pad
2. Provider creates `BoundaryEvent` business object with `attachedToRef` pointing to the host
3. Creates the appropriate event definition and sets it on the boundary event
4. Uses `elementFactory.createShape()` with the pre-built business object
5. Uses `modeling.createShape()` with `host` parameter — bpmn-js handles visual attachment
6. Positions at bottom-right of host element (`x: host.x + host.width, y: host.y + host.height`)
7. Selects the new boundary event so properties panel opens immediately

Undo/redo handled automatically by bpmn-js command stack.

## Integration

Script loading order:
```
bpmn-navigated-viewer.js  →  window.BpmnViewer
bpmn-modeler.js           →  window.BpmnModeler
boundaryEventContextPad.js →  window.boundaryEventContextPadModule
```

Modeler initialization uses defensive loading:
```javascript
additionalModules: window.boundaryEventContextPadModule
    ? [window.boundaryEventContextPadModule]
    : []
```
