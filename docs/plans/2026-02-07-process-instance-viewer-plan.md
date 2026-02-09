# Process Instance Viewer Implementation Plan

> **Status:** Implemented (2026-02-09)

**Goal:** Add a Camunda Cockpit-like process instance detail page that renders the full BPMN diagram with highlighted active/completed steps, plus detailed activity/variable/condition information.

**Architecture:** Blazor Server pages call `WorkflowEngine` which wraps Orleans grain calls. bpmn-js renders BPMN XML in the browser via JS interop. Original BPMN XML stored alongside `ProcessDefinition`. Factory grain tracks created instances per process key.

**Tech Stack:** .NET 10, Orleans 9.2.1, Blazor Server, Fluent UI, bpmn-js (CDN), JS interop

---

### Task 1: Add BpmnXml to ProcessDefinition and InstanceStateSnapshot record

**Files:**
- Modify: `src/Fleans/Fleans.Domain/ProcessDefinitions.cs`

Add `BpmnXml` property to `ProcessDefinition` (Id 5). Add `InstanceStateSnapshot`, `WorkflowInstanceInfo` records.

---

### Task 2: Add GetStateSnapshot to WorkflowInstanceState

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/IWorkflowInstanceState.cs`
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs`

Add `GetStateSnapshot()` method returning `InstanceStateSnapshot`.

---

### Task 3: Update Factory Grain — BpmnXml storage + Instance tracking

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs`
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs`

Update `DeployWorkflow` to accept `bpmnXml`. Add `_instancesByKey` and `_instanceToDefinitionId` tracking. Implement `GetInstancesByKey`, `GetBpmnXml`, `GetBpmnXmlByInstanceId`.

---

### Task 4: Update WorkflowEngine — thread BpmnXml + new query methods

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowEngine.cs`

Add `GetInstancesByKey`, `GetInstanceDetail`, `GetBpmnXml`. Update `DeployWorkflow` signature.

---

### Task 5: Update Web Upload Panel — thread BpmnXml through deploy

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/WorkflowUploadPanel.razor`

Read XML as string and pass to `DeployWorkflow`.

---

### Task 6: Fix existing tests for new DeployWorkflow signature

Update any tests calling `DeployWorkflow` directly. `RegisterWorkflow` internal calls already updated.

---

### Task 7: Add bpmn-js CDN and JS interop module

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/App.razor`
- Create: `src/Fleans/Fleans.Web/wwwroot/js/bpmnViewer.js`

Add bpmn-js CDN script. Create interop module with `init`, `highlight`, `destroy` methods.

---

### Task 8: Add bpmn-js CSS styles

**Files:**
- Modify: `src/Fleans/Fleans.Web/wwwroot/app.css`

Add `.bpmn-container`, `.bpmn-completed`, `.bpmn-active` CSS classes for diagram rendering.

---

### Task 9: Create ProcessInstances page (instance list per key)

**Files:**
- Create: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstances.razor`

`FluentDataGrid` listing instances with ID, status badges, and view link.

---

### Task 10: Create ProcessInstance page (detail with bpmn-js)

**Files:**
- Create: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor`

BPMN diagram with highlighting. Status badges. Refresh button.

---

### Task 11: Add "Instances" button to WorkflowVersionsPanel

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/WorkflowVersionsPanel.razor`

---

### Task 12: Enrich InstanceStateSnapshot with detailed activity/variable/condition data

**Files:**
- Modify: `src/Fleans/Fleans.Domain/ProcessDefinitions.cs`
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs`

Add 3 new snapshot DTOs: `ActivityInstanceSnapshot`, `VariableStateSnapshot`, `ConditionSequenceSnapshot`. Extend `InstanceStateSnapshot` with properties at Id 4-7 (ActiveActivities, CompletedActivities, VariableStates, ConditionSequences).

Rewrite `GetStateSnapshot()` to populate enriched data: fetch activity details in parallel, convert `ExpandoObject` variables to `Dictionary<string, string>`, flatten condition sequence states.

---

### Task 13: Add CompletedAt tracking to ActivityInstance

**Files:**
- Modify: `src/Fleans/Fleans.Domain/IActivityInstance.cs`
- Modify: `src/Fleans/Fleans.Domain/ActivityInstance.cs`

Add `DateTimeOffset? _completedAt` field, set in `Complete()`. Add `GetCompletedAt()` to interface. Add `CompletedAt` property to `ActivityInstanceSnapshot` (Id 7).

---

### Task 14: Add GetSnapshot() batching to IActivityInstance

**Files:**
- Modify: `src/Fleans/Fleans.Domain/IActivityInstance.cs`
- Modify: `src/Fleans/Fleans.Domain/ActivityInstance.cs`
- Modify: `src/Fleans/Fleans.Domain/States/WorkflowInstanceState.cs`

Add `[ReadOnly] ValueTask<ActivityInstanceSnapshot> GetSnapshot()` to `IActivityInstance`. Implement in `ActivityInstance` returning all fields from local state in a single synchronous call. Update `GetStateSnapshot()` to use `GetSnapshot()` instead of 7 individual grain calls per activity. This reduces grain calls from 7N to N.

---

### Task 15: Update ProcessInstance.razor with tabbed UI, sorting, and filtering

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor`

Replace simple activity lists with `FluentTabs` containing 3 tabs (Activities, Variables, Conditions). Add `FluentDataGrid` with `Sortable="true"` on `PropertyColumn`s and `SortBy` with `GridSort` on `TemplateColumn`s. Add `ColumnOptions` with `FluentSearch`/`FluentSelect` for filtering. Add `Loading` parameter to refresh button. Add error banners for failed activities. Add CompletedAt column.

---

### Task 16: Add [ReadOnly] to IWorkflowInstanceState read methods

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/IWorkflowInstanceState.cs`

Annotate all 10 read-only methods with `[ReadOnly]` from `Orleans.Concurrency`. Reorganize interface to group read-only methods above mutating methods.

---

### Task 17: Add error handling to Refresh()

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/ProcessInstance.razor`

Add try/catch to `Refresh()` matching the `LoadInstance()` pattern. Log error and set `errorMessage` for display in `FluentMessageBar`.

---

### Task 18: Add tests for GetCompletedAt and GetStateSnapshot

**Files:**
- Modify: `src/Fleans/Fleans.Domain.Tests/TaskActivityTests.cs` (5 new tests)
- Modify: `src/Fleans/Fleans.Domain.Tests/WorkflowInstanceStateTests.cs` (4 new tests)

**TaskActivityTests:**
- `GetCompletedAt` null before completion, timestamp after completion, timestamp after failure
- `GetSnapshot` returns all fields in one call, includes error state after failure

**WorkflowInstanceStateTests:**
- `GetStateSnapshot` active/completed splitting with correct activity types and timestamps
- `GetStateSnapshot` variable serialization to `Dictionary<string, string>`
- `GetStateSnapshot` condition sequences with correct paths and results
- `GetStateSnapshot` empty state returns empty lists

---

### Verification

- `dotnet build src/Fleans/` — 0 errors
- `dotnet test src/Fleans/` — 120/120 tests pass (71 Domain + 12 Application + 37 Infrastructure)
