# Structured Workflow Logging

## Date: 2026-02-08

## Problem

Workflow grains had zero logging. Event handlers had some logging but lacked consistent workflow context. No way to correlate log entries across grain boundaries for a single workflow execution.

## Key Insight

Orleans `ILogger.BeginScope` does NOT propagate across grain calls (AsyncLocal-based). The Orleans-recommended approach:
1. **`RequestContext`** — serialized into grain messages, propagates automatically
2. **Incoming grain call filter** — reads `RequestContext` and creates `BeginScope` at each grain

## Solution

### RequestContext Keys

| Key | Type | Set By |
|-----|------|--------|
| `WorkflowId` | string | WorkflowInstance |
| `ProcessDefinitionId` | string | WorkflowInstance |
| `WorkflowInstanceId` | string | WorkflowInstance |
| `ActivityId` | string | WorkflowInstance / ActivityInstance |
| `ActivityInstanceId` | string | WorkflowInstance / ActivityInstance |
| `VariablesId` | string | ActivityInstance |

### Architecture

- **`WorkflowLoggingScopeFilter`** (`IIncomingGrainCallFilter`) — reads `RequestContext` and wraps grain invocations in `BeginScope`. Skips `IWorkflowInstance` (which manages its own scope) to avoid duplication.
- **`WorkflowLoggingContext`** — static helper for event handlers (stream delivery doesn't carry `RequestContext`) that sets both `RequestContext` AND `BeginScope`.
- **`ProcessDefinitionId`** added to `IWorkflowDefinition` / `WorkflowDefinition` — stamped by `WorkflowInstanceFactoryGrain.DeployWorkflow` and carried through domain events.
- **`[LoggerMessage]` source generators** used for all log statements — zero-allocation, compile-time validated.

### BeginScope Format

Uses the string template overload (`logger.BeginScope(string, params)`) which creates `FormattedLogValues`:
- Simple formatter renders: `=> [wf-order, proc:1:..., guid, -, -, -]`
- JSON formatter emits structured key-value pairs
- `"-"` used as placeholder for null/absent values

### EventId Ranges

| Range | Class |
|-------|-------|
| 1000-1099 | WorkflowInstance |
| 2000-2099 | ActivityInstance |
| 3000-3099 | WorkflowInstanceState |
| 4000-4099 | Event handlers |
| 5000-5099 | WorkflowEventsPublisher |
| 6000-6099 | WorkflowInstanceFactoryGrain |
| 7000-7099 | WorkflowEngine |

### Filter Skip Logic

The filter skips `IWorkflowInstance` via `context.TargetContext.GrainInstance is IWorkflowInstance` because:
- WorkflowInstance creates its own `BeginWorkflowScope()` in every public method
- Without the skip, scopes would nest/duplicate
- A `RequestContext` flag approach doesn't work: the flag would propagate to downstream grains (causing them to be skipped too), and the filter runs before the grain method body (so the flag isn't set yet)

## Files

| File | Change |
|------|--------|
| `Fleans.Domain/Workflow.cs` | `ProcessDefinitionId` on interface + record |
| `Fleans.Application/Logging/WorkflowLoggingScopeFilter.cs` | NEW — grain call filter |
| `Fleans.Application/Logging/WorkflowLoggingContext.cs` | NEW — event handler helper |
| `Fleans.Api/Program.cs` | Register filter |
| `Fleans.Api/appsettings.Development.json` | Enable `IncludeScopes` |
| `Fleans.Domain/WorkflowInstance.cs` | ILogger + RequestContext + LoggerMessage |
| `Fleans.Domain/ActivityInstance.cs` | ILogger + LoggerMessage |
| `Fleans.Domain/States/WorkflowInstanceState.cs` | ILogger + LoggerMessage |
| `Fleans.Domain/Events/EvaluateConditionEvent.cs` | Added `ProcessDefinitionId` |
| `Fleans.Domain/Events/ExecuteScriptEvent.cs` | Added `ProcessDefinitionId` |
| `Fleans.Domain/Activities/ExclusiveGateway.cs` | Pass `ProcessDefinitionId` |
| `Fleans.Domain/Activities/ScriptTask.cs` | Pass `ProcessDefinitionId` |
| `Fleans.Application/Events/Handlers/*.cs` | WorkflowLoggingContext + LoggerMessage |
| `Fleans.Application/Events/WorkflowEventsPublisher.cs` | ILogger + LoggerMessage |
| `Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs` | ILogger + ProcessDefinitionId + RequestContext + LoggerMessage |
| `Fleans.Application/WorkflowEngine.cs` | ILogger + LoggerMessage |
| `Fleans.Application.Tests/WorkflowEngineTests.cs` | NullLogger |
| `Fleans.Domain/Fleans.Domain.csproj` | Explicit logging dependency |
