# Move Variable Mapping to CallActivity — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Move `BuildChildInputVariables` and `BuildParentOutputVariables` from `WorkflowInstance` grain to `CallActivity` domain record.

**Architecture:** Two static methods in the grain become instance methods on `CallActivity`. They use only `CallActivity`'s own properties — pure domain logic with no infrastructure dependencies.

**Tech Stack:** .NET 10, C# 14, MSTest, ExpandoObject

---

### Task 1: Add BuildChildInputVariables to CallActivity with tests

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/CallActivity.cs`
- Create: `src/Fleans/Fleans.Domain.Tests/CallActivityVariableMappingTests.cs`

**Step 1: Write the failing tests**

Create `src/Fleans/Fleans.Domain.Tests/CallActivityVariableMappingTests.cs`:

```csharp
using System.Dynamic;
using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class CallActivityVariableMappingTests
{
    [TestMethod]
    public void BuildChildInputVariables_PropagateAll_CopiesAllParentVariables()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "sub", [], [], PropagateAllParentVariables: true);
        var parent = new ExpandoObject();
        ((IDictionary<string, object?>)parent)["x"] = 1;
        ((IDictionary<string, object?>)parent)["y"] = "hello";

        // Act
        var result = callActivity.BuildChildInputVariables(parent);

        // Assert
        var dict = (IDictionary<string, object?>)result;
        Assert.AreEqual(2, dict.Count);
        Assert.AreEqual(1, dict["x"]);
        Assert.AreEqual("hello", dict["y"]);
    }

    [TestMethod]
    public void BuildChildInputVariables_NoPropagation_OnlyMappedVariables()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "sub",
            [new VariableMapping("x", "mapped_x")], [],
            PropagateAllParentVariables: false);
        var parent = new ExpandoObject();
        ((IDictionary<string, object?>)parent)["x"] = 42;
        ((IDictionary<string, object?>)parent)["y"] = "skipped";

        // Act
        var result = callActivity.BuildChildInputVariables(parent);

        // Assert
        var dict = (IDictionary<string, object?>)result;
        Assert.AreEqual(1, dict.Count);
        Assert.AreEqual(42, dict["mapped_x"]);
    }

    [TestMethod]
    public void BuildChildInputVariables_MappingsOverridePropagated()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "sub",
            [new VariableMapping("x", "x")], [],
            PropagateAllParentVariables: true);
        var parent = new ExpandoObject();
        ((IDictionary<string, object?>)parent)["x"] = "original";

        // Act
        var result = callActivity.BuildChildInputVariables(parent);

        // Assert — mapping runs after propagation, same key, same value
        var dict = (IDictionary<string, object?>)result;
        Assert.AreEqual(1, dict.Count);
        Assert.AreEqual("original", dict["x"]);
    }

    [TestMethod]
    public void BuildChildInputVariables_MissingSourceMapping_Skipped()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "sub",
            [new VariableMapping("missing", "target")], [],
            PropagateAllParentVariables: false);
        var parent = new ExpandoObject();

        // Act
        var result = callActivity.BuildChildInputVariables(parent);

        // Assert
        var dict = (IDictionary<string, object?>)result;
        Assert.AreEqual(0, dict.Count);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~CallActivityVariableMappingTests" --verbosity quiet 2>&1 | tail -5`

Expected: Build error — `BuildChildInputVariables` doesn't exist on `CallActivity` yet.

**Step 3: Add the method to CallActivity**

In `src/Fleans/Fleans.Domain/Activities/CallActivity.cs`, add after the existing methods:

```csharp
public ExpandoObject BuildChildInputVariables(ExpandoObject parentVariables)
{
    var result = new ExpandoObject();
    var sourceDict = (IDictionary<string, object?>)parentVariables;
    var resultDict = (IDictionary<string, object?>)result;

    if (PropagateAllParentVariables)
    {
        foreach (var kvp in sourceDict)
            resultDict[kvp.Key] = kvp.Value;
    }

    foreach (var mapping in InputMappings)
    {
        if (sourceDict.TryGetValue(mapping.Source, out var value))
            resultDict[mapping.Target] = value;
    }

    return result;
}
```

Also add `using System.Dynamic;` at the top of CallActivity.cs.

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~CallActivityVariableMappingTests" --verbosity quiet 2>&1 | tail -5`

Expected: 4 tests pass.

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/CallActivity.cs src/Fleans/Fleans.Domain.Tests/CallActivityVariableMappingTests.cs
git commit -m "feat: add BuildChildInputVariables to CallActivity with tests"
```

---

### Task 2: Add BuildParentOutputVariables to CallActivity with tests

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/CallActivity.cs`
- Modify: `src/Fleans/Fleans.Domain.Tests/CallActivityVariableMappingTests.cs`

**Step 1: Write the failing tests**

Append to `CallActivityVariableMappingTests.cs`:

```csharp
[TestMethod]
public void BuildParentOutputVariables_PropagateAll_CopiesAllChildVariables()
{
    // Arrange
    var callActivity = new CallActivity("call1", "sub", [], [], PropagateAllChildVariables: true);
    var child = new ExpandoObject();
    ((IDictionary<string, object?>)child)["a"] = 10;
    ((IDictionary<string, object?>)child)["b"] = "world";

    // Act
    var result = callActivity.BuildParentOutputVariables(child);

    // Assert
    var dict = (IDictionary<string, object?>)result;
    Assert.AreEqual(2, dict.Count);
    Assert.AreEqual(10, dict["a"]);
    Assert.AreEqual("world", dict["b"]);
}

[TestMethod]
public void BuildParentOutputVariables_NoPropagation_OnlyMappedVariables()
{
    // Arrange
    var callActivity = new CallActivity("call1", "sub", [],
        [new VariableMapping("a", "mapped_a")],
        PropagateAllChildVariables: false);
    var child = new ExpandoObject();
    ((IDictionary<string, object?>)child)["a"] = 99;
    ((IDictionary<string, object?>)child)["b"] = "skipped";

    // Act
    var result = callActivity.BuildParentOutputVariables(child);

    // Assert
    var dict = (IDictionary<string, object?>)result;
    Assert.AreEqual(1, dict.Count);
    Assert.AreEqual(99, dict["mapped_a"]);
}

[TestMethod]
public void BuildParentOutputVariables_EmptyChildVars_ReturnsEmpty()
{
    // Arrange
    var callActivity = new CallActivity("call1", "sub", [], [],
        PropagateAllChildVariables: true);
    var child = new ExpandoObject();

    // Act
    var result = callActivity.BuildParentOutputVariables(child);

    // Assert
    var dict = (IDictionary<string, object?>)result;
    Assert.AreEqual(0, dict.Count);
}
```

**Step 2: Run test to verify they fail**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~BuildParentOutputVariables" --verbosity quiet 2>&1 | tail -5`

Expected: Build error — method doesn't exist yet.

**Step 3: Add the method to CallActivity**

In `src/Fleans/Fleans.Domain/Activities/CallActivity.cs`, add after `BuildChildInputVariables`:

```csharp
public ExpandoObject BuildParentOutputVariables(ExpandoObject childVariables)
{
    var result = new ExpandoObject();
    var sourceDict = (IDictionary<string, object?>)childVariables;
    var resultDict = (IDictionary<string, object?>)result;

    if (PropagateAllChildVariables)
    {
        foreach (var kvp in sourceDict)
            resultDict[kvp.Key] = kvp.Value;
    }

    foreach (var mapping in OutputMappings)
    {
        if (sourceDict.TryGetValue(mapping.Source, out var value))
            resultDict[mapping.Target] = value;
    }

    return result;
}
```

**Step 4: Run all variable mapping tests**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~CallActivityVariableMappingTests" --verbosity quiet 2>&1 | tail -5`

Expected: All 7 tests pass.

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/CallActivity.cs src/Fleans/Fleans.Domain.Tests/CallActivityVariableMappingTests.cs
git commit -m "feat: add BuildParentOutputVariables to CallActivity with tests"
```

---

### Task 3: Update WorkflowInstance to use CallActivity methods and delete statics

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`

**Step 1: Update call site in StartChildWorkflow (line 452)**

Change:
```csharp
var childInputVars = BuildChildInputVariables(callActivity, parentVariables);
```
to:
```csharp
var childInputVars = callActivity.BuildChildInputVariables(parentVariables);
```

**Step 2: Update call site in OnChildWorkflowCompleted (line 479)**

Change:
```csharp
var mappedOutput = BuildParentOutputVariables(callActivity, childVariables);
```
to:
```csharp
var mappedOutput = callActivity.BuildParentOutputVariables(childVariables);
```

**Step 3: Delete the two static methods (lines 702-742)**

Delete the entire `BuildChildInputVariables` and `BuildParentOutputVariables` static methods from WorkflowInstance.

**Step 4: Build and run all tests**

Run: `dotnet build src/Fleans/ --verbosity quiet 2>&1 | tail -5`

Expected: 0 errors.

Run: `dotnet test src/Fleans/ --verbosity quiet 2>&1 | tail -15`

Expected: All tests pass (347 + 7 new = 354).

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "refactor: use CallActivity instance methods for variable mapping

Remove static BuildChildInputVariables and BuildParentOutputVariables
from WorkflowInstance — now domain methods on CallActivity."
```
