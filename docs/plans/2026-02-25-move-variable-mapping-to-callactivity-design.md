# Move Variable Mapping to CallActivity

**Date:** 2026-02-25
**Status:** Approved

## Problem

`WorkflowInstance` contains two static methods — `BuildChildInputVariables` and `BuildParentOutputVariables` — that are pure domain logic operating entirely on `CallActivity` properties (`InputMappings`, `OutputMappings`, `PropagateAllParentVariables`, `PropagateAllChildVariables`). These belong in the domain layer.

## Design

Move both methods to `CallActivity` as instance methods. They become:

```csharp
public ExpandoObject BuildChildInputVariables(ExpandoObject parentVariables)
public ExpandoObject BuildParentOutputVariables(ExpandoObject childVariables)
```

### Call site changes in WorkflowInstance

```csharp
// Before:
var childInputVars = BuildChildInputVariables(callActivity, parentVariables);
var mappedOutput = BuildParentOutputVariables(callActivity, childVariables);

// After:
var childInputVars = callActivity.BuildChildInputVariables(parentVariables);
var mappedOutput = callActivity.BuildParentOutputVariables(childVariables);
```

### Files affected

1. `Fleans.Domain/Activities/CallActivity.cs` — add two instance methods
2. `Fleans.Application/Grains/WorkflowInstance.cs` — delete two static methods, update two call sites
3. `Fleans.Domain.Tests/CallActivityVariableMappingTests.cs` — unit tests for the moved methods
