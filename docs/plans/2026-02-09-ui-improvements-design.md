# UI Improvements Design

## Overview

Five UI improvements to the Fleans.Web admin panel: compact navigation, resizable panels, read-only properties panel on instance detail, version-specific editing, and version-filtered instances.

## 1. FluentAppBar Navigation

Replace the 250px `FluentNavMenu` sidebar with a vertical `FluentAppBar` (~48px wide). Teams-style compact icon bar that frees ~200px of horizontal space.

**MainLayout changes:**
- Remove `FluentNavMenu` from the horizontal `FluentStack`
- Add `FluentAppBar` with two `FluentAppBarItem` entries:
  - **Workflows** — `Href="/workflows"`, icon: Flow
  - **Editor** — `Href="/editor"`, icon: DrawShape
- Home page already redirects to `/workflows`, so no Home item needed
- `FluentHeader` stays as-is

**Layout:**
```
┌──────────────────────────────────┐
│ FluentHeader ("Fleans")          │
├──┬───────────────────────────────┤
│  │                               │
│AB│  @Body (page content)         │
│  │                               │
└──┴───────────────────────────────┘
```

## 2. Vertically Resizable BPMN Viewer

Replace the fixed 600px `.bpmn-container` on the `ProcessInstance` page with a drag-handle resizable layout.

- Horizontal drag bar (~6px) between BPMN canvas and tabs section
- Visual grip indicator, `cursor: row-resize`
- Mouse drag (mousedown/mousemove/mouseup) adjusts canvas height
- Min height: 200px. Max: viewportHeight - 200px
- Height stored in Blazor variable, applied via inline style
- On drag end, call `bpmnViewer.js` to refit the diagram

**JS approach:** Small JS function handles mousedown on drag handle, tracks mousemove to compute delta, updates canvas height. On mouseup, calls Blazor callback to persist height and trigger re-render.

## 3. Read-Only Properties Panel on Instance Detail Page

Add a right-side properties panel to `ProcessInstance` page, matching the editor's panel. Horizontally resizable.

**ElementPropertiesPanel changes:**
- Add `ReadOnly` parameter (`bool`, default `false`)
- When `ReadOnly = true`: all fields `Disabled`, no type selector, no edit callbacks

**bpmnViewer.js changes:**
- Add `getElementProperties(elementId)` function that reads the element's business object from `elementRegistry`
- Returns same data shape as the editor: ID, Type, Name, ScriptFormat, Script, ConditionExpression

**Layout:**
```
┌──────────────────────────────────────────────────┐
│ PageHeader                                       │
├──────────────────────────────┬─║─┬───────────────┤
│ BPMN Canvas (resizable ↕)   │ ║ │ Properties    │
│                              │ ║ │ Panel         │
├═══════ drag handle ══════════┤ ║ │ (resizable ↔) │
│ Tabs: Activities | Variables │ ║ │ (read-only)   │
│ Conditions                   │ ║ │               │
└──────────────────────────────┴─║─┴───────────────┘
```

- Vertical drag bar (~6px, `cursor: col-resize`) between content and panel
- Default panel width: 300px. Min: 200px. Max: 50% viewport width
- Both drag handles work independently

## 4. Edit Any Version of BPMN

Currently the Editor always loads the latest version. Allow editing any deployed version.

**Route changes:**
- New route: `@page "/editor/{ProcessDefinitionKey}/{Version:int}"`
- Existing route `@page "/editor/{ProcessDefinitionKey}"` stays (defaults to latest)

**WorkflowEngine changes:**
- New method: `GetBpmnXmlByKeyAndVersion(string key, int version)`

**WorkflowInstanceFactoryGrain changes:**
- New grain method to look up a specific version's BPMN XML

**Editor UI changes:**
- Toolbar shows version badge: `"Editing v{Version} of {ProcessDefinitionKey}"`
- Deploy dialog shows: `"Will deploy as v{nextVersion}"` — always deploys as newest version
- "Edit" button on Workflows page links to `/editor/{Key}/{Version}` for that row's version

## 5. Instances Filtered by Version

Each row on the Workflows page represents a specific version. Clicking "Instances" filters by that version.

**Route changes:**
- New route: `@page "/process-instances/{ProcessDefinitionKey}/{Version:int}"`
- Existing route stays (shows all versions)

**WorkflowEngine changes:**
- New method: `GetInstancesByKeyAndVersion(string key, int version)`

**WorkflowInstanceFactoryGrain changes:**
- New grain method to filter instances by version
- Already tracks which definition created which instance — needs version filter

**ProcessInstances UI changes:**
- Page header: `"Instances — {Key} v{Version}"` when filtered, `"Instances — {Key} (all versions)"` when unfiltered
- "Instances" button on each Workflows row navigates to `/process-instances/{Key}/{Version}`

## Out of Scope

- Persisting panel sizes across sessions (localStorage)
- Dark mode / theme switching
- Mobile responsive layout
- Instance version column on the all-versions view
- Adding a `Version` field to `WorkflowInstanceInfo` (filtering at query level is sufficient)
