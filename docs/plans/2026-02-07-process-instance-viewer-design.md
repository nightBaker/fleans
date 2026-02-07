# Process Instance Viewer Design

**Status:** Draft (validated 2026-02-07)

## Goal

Add a process instance detail page to the Blazor Web app that visualizes a BPMN diagram with highlighted current/completed steps — similar to Camunda Cockpit.

## Navigation Flow

```
Workflows page → "Instances" button on version row → Instance list → Click instance → Instance detail page
```

## Approach

- **Rendering:** bpmn-js (same library Camunda uses) via Blazor JS interop
- **Communication:** Blazor Server calls `WorkflowEngine` directly (no new API endpoints)
- **BPMN XML:** Stored in `ProcessDefinition` record alongside the parsed `WorkflowDefinition`

## Data Layer Changes

### Store BPMN XML

`ProcessDefinition` record gains a `BpmnXml` field:

```csharp
record ProcessDefinition(
    string ProcessDefinitionId,
    string ProcessDefinitionKey,
    int Version,
    DateTimeOffset DeployedAt,
    WorkflowDefinition Workflow,
    string BpmnXml);           // NEW
```

The deploy flow threads the raw XML through:
- `WorkflowEngine.DeployWorkflow(WorkflowDefinition, string bpmnXml)`
- `IWorkflowInstanceFactoryGrain.DeployWorkflow(WorkflowDefinition, string bpmnXml)`
- Stored in the factory grain's `_byId` / `_byKey` dictionaries

### Track Instances Per Process Key

`WorkflowInstanceFactoryGrain` adds:

```csharp
Dictionary<string, List<Guid>> _instancesByKey;   // processDefinitionKey → instance IDs
Dictionary<Guid, string> _instanceToDefinitionId;  // instanceId → processDefinitionId
```

Populated in `CreateWorkflowInstanceGrain()` / `CreateWorkflowInstanceGrainByProcessDefinitionId()`.

New grain methods:
- `GetInstancesByKey(string key) → List<WorkflowInstanceInfo>`
- `GetBpmnXml(string processDefinitionId) → string`

### Expose Instance State

`IWorkflowInstanceState` gets a new method:

```csharp
ValueTask<InstanceStateSnapshot> GetStateSnapshot();
```

Returns:
```csharp
record InstanceStateSnapshot(
    List<string> ActiveActivityIds,
    List<string> CompletedActivityIds,
    bool IsStarted,
    bool IsCompleted);
```

`WorkflowEngine` gets new methods:
- `GetInstancesByKey(string key) → List<WorkflowInstanceInfo>`
- `GetInstanceDetail(Guid instanceId) → WorkflowInstanceDetail`
- `GetBpmnXml(Guid instanceId) → string`

## UI Layer Changes

### WorkflowVersionsPanel.razor — Add "Instances" button

Each version row gets an "Instances" button. Clicking it navigates to an instances list or expands inline to show instances for that version.

### New Page: ProcessInstance.razor — Route: `/process-instance/{instanceId:guid}`

**Layout:**
- Header: Instance ID, status badge (Running / Completed), process key, "Back" link
- Main: bpmn-js canvas rendering the full BPMN diagram
- Footer/sidebar: Activity timeline (completed activities in order), variables

**bpmn-js integration:**
- Load bpmn-js via CDN (`<script>` in `App.razor`)
- `wwwroot/js/bpmnViewer.js` — interop module:
  - `initViewer(containerId, bpmnXml)` — create viewer, import XML, fit viewport
  - `highlightActivities(completedIds, activeIds)` — add CSS overlays
- Colors: green = completed, blue = active, gray = default
- Called from Blazor via `IJSRuntime.InvokeVoidAsync`

### New Page: ProcessInstances.razor — Route: `/process-instances/{processDefinitionKey}`

Lists all instances for a given process key. Table columns:
- Instance ID
- Status (Running / Completed)
- Link to detail page

## File Changes

| File | Change |
|------|--------|
| `Fleans.Domain/ProcessDefinitions.cs` | Add `BpmnXml` to `ProcessDefinition`, add `InstanceStateSnapshot` record |
| `Fleans.Domain/States/IWorkflowInstanceState.cs` | Add `GetStateSnapshot()` |
| `Fleans.Domain/States/WorkflowInstanceState.cs` | Implement `GetStateSnapshot()` |
| `Fleans.Domain/IWorkflowInstance.cs` | Add `GetInstanceInfo()` method |
| `Fleans.Domain/WorkflowInstance.cs` | Implement `GetInstanceInfo()` |
| `Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs` | Add `GetInstancesByKey()`, `GetBpmnXml()`, update `DeployWorkflow` signature |
| `Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs` | Track instances, store XML, implement new methods |
| `Fleans.Application/WorkflowEngine.cs` | Add `GetInstancesByKey()`, `GetInstanceDetail()`, `GetBpmnXml()`, update `DeployWorkflow()` |
| `Fleans.Web/Components/Pages/Workflows.razor` | Pass deploy XML through |
| `Fleans.Web/Components/WorkflowVersionsPanel.razor` | Add "Instances" button |
| `Fleans.Web/Components/WorkflowUploadPanel.razor` | Thread XML to `DeployWorkflow()` |
| `Fleans.Web/Components/Pages/ProcessInstances.razor` | **NEW** — Instance list page |
| `Fleans.Web/Components/Pages/ProcessInstance.razor` | **NEW** — Instance detail with bpmn-js |
| `Fleans.Web/wwwroot/js/bpmnViewer.js` | **NEW** — JS interop for bpmn-js |
| `Fleans.Web/Components/Layout/NavMenu.razor` | Add nav link for instances (optional) |
| `Fleans.Web/Components/App.razor` | Add bpmn-js CDN script tag |

## Not In Scope

- Persistence (state remains in-memory)
- Real-time updates / auto-refresh
- Instance filtering/search beyond per-key listing
- Variable editing from the UI
