# Phase 2: Nested Scopes — Embedded Sub-Process (2.1–2.3)

**Goal:** Support BPMN `<subProcess>` — activities nested inside a sub-process scope with variable inheritance, scope-aware execution, and boundary event cancellation.

**Scope:** Items 2.1 (tree-structured definition), 2.2 (variable scope chain), 2.3 (embedded sub-process execution). Multi-instance (2.4–2.5) deferred.

---

## Key Design Decisions

1. **Same grain execution** — SubProcess runs within the parent WorkflowInstance grain. No child grain spawning (unlike CallActivity). Simpler variable sharing, atomic state transitions.
2. **Walk-up variable chain** — Each scope has a `ParentVariablesId`. `GetVariable` checks local scope first, walks up if not found. Writes always go to local scope (shadowing).
3. **ScopeId on ActivityInstanceEntry** — Tracks which sub-process scope spawned each activity. Root-level entries have `ScopeId = null`. Cancellation = cancel all entries with matching ScopeId.
4. **SubProcess holds child lists** — `SubProcess` record has its own `List<Activity>` and `List<SequenceFlow>`. Implements `IWorkflowDefinition` so existing code works unchanged.

---

## Domain Model Changes

### SubProcess Activity

```csharp
[GenerateSerializer]
public record SubProcess(string ActivityId) : BoundarableActivity(ActivityId), IWorkflowDefinition
{
    [Id(1)] public List<Activity> Activities { get; init; } = [];
    [Id(2)] public List<SequenceFlow> SequenceFlows { get; init; } = [];

    // IWorkflowDefinition implementation — reuse same resolution logic
    public string WorkflowId => ActivityId;
    public Activity GetActivity(string activityId) => Activities.First(a => a.ActivityId == activityId);
}
```

SubProcess extends `BoundarableActivity` (supports boundary events) and implements `IWorkflowDefinition` so all existing code that takes `IWorkflowDefinition` (activity `ExecuteAsync`, `GetNextActivities`) works unchanged.

### Variable Scope Chain

`WorkflowVariablesState` gets a parent pointer:

```
WorkflowVariablesState
├── Id: Guid
├── ParentVariablesId: Guid?    // NEW — null for root scope
├── WorkflowInstanceId: Guid
└── Variables: ExpandoObject
```

- `GetVariable(variablesId, name)`: check local scope, if not found and `ParentVariablesId != null`, recurse up.
- `SetVariable(variablesId, name, value)`: always writes to local scope (shadowing).

### ActivityInstanceEntry Scope Tracking

```
ActivityInstanceEntry
├── ... existing fields ...
└── ScopeId: Guid?    // NEW — null for root, sub-process entry's ActivityInstanceId for nested
```

---

## Execution Model

### SubProcess Lifecycle

1. **Entry:** Execution loop encounters SubProcess. `SubProcess.ExecuteAsync()` creates a child variable scope (`ParentVariablesId` → current scope), then activates the sub-process's internal StartEvent. Child entries tagged with `ScopeId = subProcessEntry.ActivityInstanceId`.

2. **Running:** Existing execution loop iterates all active entries. Sub-process children execute naturally. Key change: `TransitionToNextActivity` resolves next activities from the **correct definition** — SubProcess's own lists, not the parent's.

3. **Completion:** When sub-process's EndEvent completes and all scope activities are done, the SubProcess entry itself completes. `TransitionToNextActivity` then moves to the next parent-level activity.

4. **Boundary interruption:** Boundary event fires → cancel all entries where `ScopeId == subProcessEntry.ActivityInstanceId`. Reuses existing cancellation infrastructure.

### Definition Resolution

The execution loop needs to know which `IWorkflowDefinition` an activity belongs to:

```
GetDefinitionForActivity(activityId) → IWorkflowDefinition
```

Walks the definition tree: check root activities, recurse into each SubProcess. Returns the SubProcess (or root WorkflowDefinition) that contains the activity.

### No Changes to ActivityInstance Grain

ActivityInstance stays a simple state machine. All scope awareness lives in WorkflowInstance.

---

## BpmnConverter Changes

Switch from `.Descendants()` (flattens everything) to `.Elements()` (direct children only) for scope-aware parsing:

```
ParseActivities(XElement scopeElement, ...)
    foreach <startEvent> in scopeElement.Elements(...)
    foreach <task> in scopeElement.Elements(...)
    foreach <subProcess> in scopeElement.Elements(...)
        → create SubProcess activity
        → recursively call ParseActivities(subProcessElement, ...)
        → attach child activities and flows to SubProcess
    foreach <sequenceFlow> in scopeElement.Elements(...)
```

Global `activityMap` (ID → Activity) stays flat for sequence flow resolution. Each scope's activities and flows are stored on the correct definition (root or SubProcess).

**BPMN XML structure:**
```xml
<process id="p1">
  <startEvent id="start"/>
  <subProcess id="sub1">
    <startEvent id="sub1_start"/>
    <task id="sub1_task"/>
    <endEvent id="sub1_end"/>
    <sequenceFlow sourceRef="sub1_start" targetRef="sub1_task"/>
    <sequenceFlow sourceRef="sub1_task" targetRef="sub1_end"/>
  </subProcess>
  <endEvent id="end"/>
  <sequenceFlow sourceRef="start" targetRef="sub1"/>
  <sequenceFlow sourceRef="sub1" targetRef="end"/>
</process>
```

---

## Testing Strategy

### Domain Tests
- SubProcess `ExecuteAsync` creates child variable scope and activates internal start event
- SubProcess `GetNextActivities` returns outgoing flows from parent scope
- Variable scope walk-up: child reads parent variable, child write shadows parent

### Infrastructure Tests
- BpmnConverter parses `<subProcess>` with nested activities and internal flows
- Nested sub-processes (sub-process inside sub-process) parse correctly
- Boundary events on sub-process parse correctly

### Integration Tests
- Simple sub-process: Start → SubProcess(Start → Task → End) → End — completes through
- Variable inheritance: parent sets var, sub-process task reads it
- Variable shadowing: sub-process writes same var name, parent var unchanged
- Boundary timer on sub-process: timer fires, all child activities cancelled, workflow continues on boundary path
- Boundary error on sub-process: child task fails, error boundary catches, sub-process cancelled
- Nested sub-process: sub-process inside sub-process executes and completes
