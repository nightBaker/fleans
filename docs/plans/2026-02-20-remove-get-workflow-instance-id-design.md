# Remove GetWorkflowInstanceId from Interfaces

## Problem

`GetWorkflowInstanceId()` on `IWorkflowExecutionContext` wraps a synchronous value (`this.GetPrimaryKey()`) in `ValueTask<Guid>`, adding unnecessary async overhead. After removing `GetWorkflowDefinition()` from the context, keeping `GetWorkflowInstanceId()` is inconsistent — both are grain-level data that should be passed as parameters.

## Decision

Remove `GetWorkflowInstanceId()` from `IWorkflowExecutionContext` and `IWorkflowInstanceGrain`. Pass `Guid workflowInstanceId` as a parameter to `Activity.ExecuteAsync`. Application callers use `GetPrimaryKey()` directly.

## Changes

### Interfaces
- `IWorkflowExecutionContext`: remove `GetWorkflowInstanceId()`
- `IWorkflowInstanceGrain`: remove `new GetWorkflowInstanceId()` re-declaration

### Activity methods
- `Activity.ExecuteAsync`: add `Guid workflowInstanceId` as 4th parameter
- `GetNextActivities` / `RegisterBoundaryEventsAsync`: unchanged (don't use it)

### Domain activities
- All `ExecuteAsync` overrides: add param, pass to `base.ExecuteAsync()`
- `ScriptTask`: use param directly for `ExecuteScriptEvent`
- `ExclusiveGateway`: use param directly for `EvaluateConditionEvent`

### Application layer
- `WorkflowInstance.ExecuteWorkflow()`: pass `this.GetPrimaryKey()` to `ExecuteAsync`
- `WorkflowInstance.GetWorkflowInstanceId()`: delete
- `WorkflowCommandService`: replace with `GetPrimaryKey()`
- `BoundaryEventHandler.CreateAndExecuteBoundaryInstanceAsync`: pass `_accessor.State.Id`

### Tests
- `ActivityTestHelper`: remove `GetWorkflowInstanceId()` mock
- All domain tests calling `ExecuteAsync`: add `Guid.NewGuid()` or specific ID
- Remove `GetWorkflowInstanceId_ShouldReturnCorrectGuid` test
- `WorkflowCommandServiceTests`: use `GetPrimaryKey()`
