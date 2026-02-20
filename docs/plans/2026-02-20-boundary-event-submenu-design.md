# Boundary Event Context Pad Submenu Design

## Goal

Replace the 3 individual boundary event buttons (Timer, Message, Error) in the bpmn-js context pad with a single button that opens a popup submenu containing those options.

## Current Behavior

When clicking a boundaryable activity (Task, ScriptTask, etc.), the context pad shows 3 separate buttons directly:
- Timer Boundary Event
- Message Boundary Event
- Error Boundary Event

This clutters the context pad, especially as more boundary event types are added.

## Proposed Behavior

A single "Attach Boundary Event" button appears in the context pad. Clicking it opens a bpmn-js popup menu with the 3 options. User selects one, and the boundary event is created — same as today.

## Approach: bpmn-js PopupMenu

Use bpmn-js's built-in `popupMenu` service — the same infrastructure used by the "change type" wrench button. This handles positioning, keyboard navigation, and dismiss-on-click-away automatically.

### Changes

**Single file change:** `src/Fleans/Fleans.Web/wwwroot/js/boundaryEventContextPad.js`

1. Inject `popupMenu` dependency
2. Register a `PopupMenuProvider` for `'boundary-event'` menu ID
3. Replace 3 context pad entries with 1 entry whose click handler opens the popup menu
4. Each popup entry's action calls `_createBoundaryEvent()`

No changes to `bpmnEditor.js`, `Editor.razor`, or backend code.
