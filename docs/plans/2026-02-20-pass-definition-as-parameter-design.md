# Pass IWorkflowDefinition as Parameter to Activities

## Problem

Every domain activity calls `workflowContext.GetWorkflowDefinition()` to obtain the workflow definition. This adds an unnecessary async hop per call, couples activities to a fetch-from-context pattern, and bloats `IWorkflowExecutionContext` with a method that belongs to the infrastructure layer.

`WorkflowInstance` already lazy-loads and caches the definition from `IProcessDefinitionGrain`. Activities should receive it as a parameter instead of re-fetching it.

## Decision

Remove `GetWorkflowDefinition()` from `IWorkflowExecutionContext` and `IBoundaryEventStateAccessor`. Add `IWorkflowDefinition definition` as a parameter to `Activity.ExecuteAsync`, `Activity.GetNextActivities`, `IBoundarableActivity.RegisterBoundaryEventsAsync`, and `BoundaryEventHandler` public methods.

`WorkflowInstance` resolves the definition once via `IProcessDefinitionGrain` (existing `EnsureWorkflowDefinitionAsync` + `_workflowDefinition` cache) and passes it down to all call sites.

## Changes

### Interfaces

- `IWorkflowExecutionContext`: remove `GetWorkflowDefinition()`
- `IBoundaryEventStateAccessor`: remove `GetWorkflowDefinition()`
- `IBoundarableActivity.RegisterBoundaryEventsAsync`: add `IWorkflowDefinition definition` parameter
- `Activity.ExecuteAsync`: add `IWorkflowDefinition definition` parameter
- `Activity.GetNextActivities`: add `IWorkflowDefinition definition` parameter
- `IBoundaryEventHandler` public methods: add `IWorkflowDefinition definition` parameter

### WorkflowInstance

- All internal call sites that called `await GetWorkflowDefinition()` use cached `_workflowDefinition` field or accept definition as parameter
- Passes definition to `ExecuteAsync`, `GetNextActivities`, `RegisterBoundaryEventsAsync`, and `BoundaryEventHandler` methods

### Domain Activities (~15 files)

Each override of `ExecuteAsync` and `GetNextActivities` gains the `IWorkflowDefinition definition` parameter. Internal `await workflowContext.GetWorkflowDefinition()` calls replaced with direct `definition` usage.

### BoundaryEventHandler

Each public method gains `IWorkflowDefinition definition` parameter. Internal `_accessor.GetWorkflowDefinition()` calls replaced with direct `definition` usage.

### Tests

- `ActivityTestHelper`: remove `context.GetWorkflowDefinition()` mock, pass definition as parameter
- `BoundaryEventHandlerTests`: remove `_accessor.GetWorkflowDefinition()` mock, pass definition to handler methods
- `IWorkflowInstanceGrain.GetWorkflowDefinition()` remains as a public grain method (unchanged)

## Files Affected

| File | Action |
|------|--------|
| `Fleans.Domain/IWorkflowExecutionContext.cs` | Remove `GetWorkflowDefinition()` |
| `Fleans.Domain/Activities/Activity.cs` | Add param to `ExecuteAsync`, `GetNextActivities` |
| `Fleans.Domain/IBoundarableActivity.cs` | Add param to `RegisterBoundaryEventsAsync` |
| `Fleans.Domain/Activities/BoundarableActivity.cs` | Add param, use directly |
| `Fleans.Domain/Activities/StartEvent.cs` | Add param, use directly |
| `Fleans.Domain/Activities/EndEvent.cs` | Add param |
| `Fleans.Domain/Activities/ScriptTask.cs` | Add param, use directly |
| `Fleans.Domain/Activities/TaskActivity.cs` | Add param, use directly |
| `Fleans.Domain/Activities/CallActivity.cs` | Add param, use directly |
| `Fleans.Domain/Activities/ExclusiveGateway.cs` | Add param, use directly |
| `Fleans.Domain/Activities/ConditionalGateway.cs` | Add param, use directly |
| `Fleans.Domain/Activities/ParallelGateway.cs` | Add param, use directly |
| `Fleans.Domain/Activities/ErrorEvent.cs` | Add param, use directly |
| `Fleans.Domain/Activities/BoundaryErrorEvent.cs` | Add param, use directly |
| `Fleans.Domain/Activities/BoundaryTimerEvent.cs` | Add param, use directly |
| `Fleans.Domain/Activities/MessageBoundaryEvent.cs` | Add param, use directly |
| `Fleans.Domain/Activities/TimerStartEvent.cs` | Add param, use directly |
| `Fleans.Domain/Activities/TimerIntermediateCatchEvent.cs` | Add param, use directly |
| `Fleans.Domain/Activities/MessageIntermediateCatchEvent.cs` | Add param, use directly |
| `Fleans.Application/Grains/WorkflowInstance.cs` | Pass definition to calls, use cached field |
| `Fleans.Application/Services/IBoundaryEventStateAccessor.cs` | Remove `GetWorkflowDefinition()` |
| `Fleans.Application/Services/IBoundaryEventHandler.cs` | Add param to public methods |
| `Fleans.Application/Services/BoundaryEventHandler.cs` | Add param, use directly |
| `Fleans.Domain.Tests/ActivityTestHelper.cs` | Remove mock, pass param |
| `Fleans.Application.Tests/BoundaryEventHandlerTests.cs` | Remove mock, pass param |
