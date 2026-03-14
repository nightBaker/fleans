# Start Event Unregistration & Process Disable/Enable — Implementation Plan

> **Note:** This is a historical plan document. Code snippets below may diverge from the actual implementation — refer to the source code for the current state.

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix timer start event leak on redeployment and add soft disable/enable for process definitions.

**Architecture:** Add `IsActive` flag to `ProcessDefinition`, expose `DisableProcess`/`EnableProcess` on the factory grain, add timer deactivation in `DeployWorkflow`, guard `GetLatestWorkflowDefinition` against disabled processes, and expose via API + Web UI.

**Tech Stack:** C# 14, .NET 10, Orleans 9, MSTest, Fluent UI Blazor

**Design doc:** `docs/plans/2026-03-07-start-event-unregistration-design.md`

---

### Task 1: Add `IsActive` to `ProcessDefinition` domain model

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Definitions/ProcessDefinition.cs:9-40`
- Modify: `src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs:208-239`

**Step 1: Add IsActive property to ProcessDefinition**

In `ProcessDefinition.cs`, add after the `ETag` property (line 39):

```csharp
[Id(7)]
public bool IsActive { get; set; } = true;
```

**Step 2: Add IsActive column to EF Core config**

In `FleanModelConfiguration.cs`, inside the `ProcessDefinition` entity block (after line 218, the index line), add:

```csharp
entity.Property(e => e.IsActive).HasDefaultValue(true);
```

**Step 3: Verify build**

Run: `dotnet build src/Fleans/`
Expected: BUILD SUCCEEDED

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/Definitions/ProcessDefinition.cs src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs
git commit -m "feat: add IsActive flag to ProcessDefinition"
```

---

### Task 2: Add `IsActive` to `ProcessDefinitionSummary` and update query service

**Files:**
- Modify: `src/Fleans/Fleans.Application/QueryModels/ProcessDefinitionSummary.cs:6-12`
- Modify: `src/Fleans/Fleans.Persistence/WorkflowQueryService.cs:97-103`
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs:235-242`

**Step 1: Add IsActive to ProcessDefinitionSummary record**

Replace the record in `ProcessDefinitionSummary.cs` with:

```csharp
[GenerateSerializer]
public sealed record ProcessDefinitionSummary(
    [property: Id(0)] string ProcessDefinitionId,
    [property: Id(1)] string ProcessDefinitionKey,
    [property: Id(2)] int Version,
    [property: Id(3)] DateTimeOffset DeployedAt,
    [property: Id(4)] int ActivitiesCount,
    [property: Id(5)] int SequenceFlowsCount,
    [property: Id(6)] bool IsActive = true);
```

**Step 2: Update `ToSummary` in WorkflowInstanceFactoryGrain**

In `WorkflowInstanceFactoryGrain.cs`, update the `ToSummary` method (lines 235-242):

```csharp
private static ProcessDefinitionSummary ToSummary(ProcessDefinition definition) =>
    new(
        ProcessDefinitionId: definition.ProcessDefinitionId,
        ProcessDefinitionKey: definition.ProcessDefinitionKey,
        Version: definition.Version,
        DeployedAt: definition.DeployedAt,
        ActivitiesCount: definition.Workflow.Activities.Count,
        SequenceFlowsCount: definition.Workflow.SequenceFlows.Count,
        IsActive: definition.IsActive);
```

**Step 3: Update WorkflowQueryService.GetAllProcessDefinitions**

In `WorkflowQueryService.cs`, update the `Select` projection (around line 97-103) to include `IsActive`:

```csharp
return definitions.Select(d => new ProcessDefinitionSummary(
    d.ProcessDefinitionId,
    d.ProcessDefinitionKey,
    d.Version,
    d.DeployedAt,
    d.Workflow.Activities.Count,
    d.Workflow.SequenceFlows.Count,
    d.IsActive)).ToList();
```

**Step 4: Verify build**

Run: `dotnet build src/Fleans/`
Expected: BUILD SUCCEEDED

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application/QueryModels/ProcessDefinitionSummary.cs \
        src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs \
        src/Fleans/Fleans.Persistence/WorkflowQueryService.cs
git commit -m "feat: add IsActive to ProcessDefinitionSummary and query service"
```

---

### Task 3: Fix timer deactivation on redeployment

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs:148-153`

**Step 1: Write the failing test**

In `src/Fleans/Fleans.Application.Tests/WorkflowInstanceFactoryGrainTests.cs`, add a new test method after the existing tests (before `CreateSimpleWorkflow` helper):

```csharp
[TestMethod]
public async Task DeployWorkflow_ShouldDeactivateTimer_WhenNewVersionRemovesTimerStartEvent()
{
    // Arrange — deploy v1 with a TimerStartEvent
    var processKey = "timer-removal-test";
    var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

    var timerStart = new TimerStartEvent("timerStart")
    {
        TimerDefinition = new TimerDefinition(TimerType.Duration, "PT1H")
    };
    var end1 = new EndEvent("end1");
    var v1 = new WorkflowDefinition
    {
        WorkflowId = processKey,
        Activities = new List<Activity> { timerStart, end1 },
        SequenceFlows = new List<SequenceFlow>
        {
            new SequenceFlow("seq1", timerStart, end1)
        }
    };
    await factoryGrain.DeployWorkflow(v1, "<bpmn/>");

    // Act — deploy v2 WITHOUT a TimerStartEvent
    var start = new StartEvent("start");
    var end2 = new EndEvent("end2");
    var v2 = new WorkflowDefinition
    {
        WorkflowId = processKey,
        Activities = new List<Activity> { start, end2 },
        SequenceFlows = new List<SequenceFlow>
        {
            new SequenceFlow("seq1", start, end2)
        }
    };
    await factoryGrain.DeployWorkflow(v2, "<bpmn/>");

    // Assert — the scheduler grain should have been deactivated (no reminder)
    // We verify indirectly: FireTimerStartEvent on v2 should fail because
    // v2 has no TimerStartEvent
    var scheduler = _cluster.GrainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processKey);
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
    {
        await scheduler.FireTimerStartEvent();
    });
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Fleans/ --filter "DeployWorkflow_ShouldDeactivateTimer"`
Expected: FAIL — the scheduler still has state from v1 and fires successfully instead of throwing

**Step 3: Add timer deactivation else-branch**

In `WorkflowInstanceFactoryGrain.cs`, replace lines 148-153 with:

```csharp
// Activate or deactivate timer scheduler based on whether new version has a TimerStartEvent
if (workflowWithId.Activities.OfType<TimerStartEvent>().Any())
{
    var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
    await scheduler.ActivateScheduler(processDefinitionId);
}
else if (versions.Count > 1 && versions[^2].Workflow.Activities.OfType<TimerStartEvent>().Any())
{
    var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
    await scheduler.DeactivateScheduler();
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Fleans/ --filter "DeployWorkflow_ShouldDeactivateTimer"`
Expected: PASS

**Step 5: Run all tests to check no regressions**

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs \
        src/Fleans/Fleans.Application.Tests/WorkflowInstanceFactoryGrainTests.cs
git commit -m "fix: deactivate timer scheduler when new version removes TimerStartEvent"
```

---

### Task 4: Add `DisableProcess` / `EnableProcess` to factory grain interface

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs:8-16`

**Step 1: Add new methods to the interface**

In `IWorkflowInstanceFactoryGrain.cs`, add after the `GetLatestWorkflowDefinition` method (before the closing brace):

```csharp
Task<ProcessDefinitionSummary> DisableProcess(string processDefinitionKey);
Task<ProcessDefinitionSummary> EnableProcess(string processDefinitionKey);
```

**Step 2: Verify build fails (methods not implemented yet)**

Run: `dotnet build src/Fleans/`
Expected: BUILD FAILED — `WorkflowInstanceFactoryGrain` does not implement the new methods

**Step 3: Commit (interface only)**

This step is deferred — we commit together with the implementation in Task 5.

---

### Task 5: Implement `DisableProcess` / `EnableProcess` in the factory grain

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs`

**Step 1: Write the failing tests**

In `src/Fleans/Fleans.Application.Tests/WorkflowInstanceFactoryGrainTests.cs`, add:

```csharp
[TestMethod]
public async Task DisableProcess_ShouldSetIsActiveFalse()
{
    // Arrange
    var processKey = "disable-test";
    var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
    await factoryGrain.DeployWorkflow(CreateSimpleWorkflow(processKey), "<bpmn/>");

    // Act
    var summary = await factoryGrain.DisableProcess(processKey);

    // Assert
    Assert.IsFalse(summary.IsActive);
}

[TestMethod]
public async Task EnableProcess_ShouldSetIsActiveTrue()
{
    // Arrange
    var processKey = "enable-test";
    var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
    await factoryGrain.DeployWorkflow(CreateSimpleWorkflow(processKey), "<bpmn/>");
    await factoryGrain.DisableProcess(processKey);

    // Act
    var summary = await factoryGrain.EnableProcess(processKey);

    // Assert
    Assert.IsTrue(summary.IsActive);
}

[TestMethod]
public async Task DisableProcess_ShouldBlockNewInstances()
{
    // Arrange
    var processKey = "disable-block-test";
    var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
    await factoryGrain.DeployWorkflow(CreateSimpleWorkflow(processKey), "<bpmn/>");
    await factoryGrain.DisableProcess(processKey);

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
    {
        await factoryGrain.CreateWorkflowInstanceGrain(processKey);
    });
}

[TestMethod]
public async Task DeployWorkflow_ShouldAutoEnable_WhenProcessWasDisabled()
{
    // Arrange
    var processKey = "auto-enable-test";
    var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
    await factoryGrain.DeployWorkflow(CreateSimpleWorkflow(processKey), "<bpmn/>");
    await factoryGrain.DisableProcess(processKey);

    // Act — deploy a new version
    var summary = await factoryGrain.DeployWorkflow(CreateSimpleWorkflow(processKey), "<bpmn/>");

    // Assert
    Assert.IsTrue(summary.IsActive);
    Assert.AreEqual(2, summary.Version);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/ --filter "DisableProcess|EnableProcess|DeployWorkflow_ShouldAutoEnable"`
Expected: FAIL — methods not implemented

**Step 3: Implement DisableProcess**

In `WorkflowInstanceFactoryGrain.cs`, add after the `GetLatestWorkflowDefinition` method (after line 218):

```csharp
public async Task<ProcessDefinitionSummary> DisableProcess(string processDefinitionKey)
{
    var definition = GetLatestDefinitionOrThrow(processDefinitionKey);

    if (!definition.IsActive)
        return ToSummary(definition);

    definition.IsActive = false;
    await _repository.SaveAsync(definition);
    LogProcessDisabled(processDefinitionKey);

    await UnregisterAllStartEventListeners(definition.Workflow, processDefinitionKey);

    return ToSummary(definition);
}

public async Task<ProcessDefinitionSummary> EnableProcess(string processDefinitionKey)
{
    var definition = GetLatestDefinitionOrThrow(processDefinitionKey);

    if (definition.IsActive)
        return ToSummary(definition);

    definition.IsActive = true;
    await _repository.SaveAsync(definition);
    LogProcessEnabled(processDefinitionKey);

    await RegisterAllStartEventListeners(definition.Workflow, processDefinitionKey, definition.ProcessDefinitionId);

    return ToSummary(definition);
}
```

**Step 4: Extract helper methods for register/unregister**

Add these private methods to `WorkflowInstanceFactoryGrain.cs` (before `GenerateProcessDefinitionId`):

```csharp
private async Task UnregisterAllStartEventListeners(IWorkflowDefinition workflow, string processDefinitionKey)
{
    // Timer
    if (workflow.Activities.OfType<TimerStartEvent>().Any())
    {
        var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
        await scheduler.DeactivateScheduler();
    }

    // Messages
    foreach (var messageStart in workflow.Activities.OfType<MessageStartEvent>())
    {
        var msgDef = workflow.Messages.FirstOrDefault(m => m.Id == messageStart.MessageDefinitionId);
        if (msgDef != null)
        {
            var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(msgDef.Name);
            await listener.UnregisterProcess(processDefinitionKey);
        }
    }

    // Signals
    foreach (var signalStart in workflow.Activities.OfType<SignalStartEvent>())
    {
        var sigDef = workflow.Signals.FirstOrDefault(s => s.Id == signalStart.SignalDefinitionId);
        if (sigDef != null)
        {
            var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(sigDef.Name);
            await listener.UnregisterProcess(processDefinitionKey);
        }
    }
}

private async Task RegisterAllStartEventListeners(IWorkflowDefinition workflow, string processDefinitionKey, string processDefinitionId)
{
    // Timer
    if (workflow.Activities.OfType<TimerStartEvent>().Any())
    {
        var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
        await scheduler.ActivateScheduler(processDefinitionId);
    }

    // Messages
    foreach (var messageStart in workflow.Activities.OfType<MessageStartEvent>())
    {
        var msgDef = workflow.Messages.FirstOrDefault(m => m.Id == messageStart.MessageDefinitionId);
        if (msgDef != null)
        {
            var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(msgDef.Name);
            await listener.RegisterProcess(processDefinitionKey);
        }
    }

    // Signals
    foreach (var signalStart in workflow.Activities.OfType<SignalStartEvent>())
    {
        var sigDef = workflow.Signals.FirstOrDefault(s => s.Id == signalStart.SignalDefinitionId);
        if (sigDef != null)
        {
            var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(sigDef.Name);
            await listener.RegisterProcess(processDefinitionKey);
        }
    }
}
```

**Step 5: Add guard to GetLatestWorkflowDefinition and CreateWorkflowInstanceGrain**

In `GetLatestDefinitionOrThrow` (line 220-233), add after the existing checks:

```csharp
private ProcessDefinition GetLatestDefinitionOrThrow(string processDefinitionKey, bool allowDisabled = false)
{
    if (string.IsNullOrWhiteSpace(processDefinitionKey))
    {
        throw new ArgumentException("ProcessDefinitionKey cannot be null or empty.", nameof(processDefinitionKey));
    }

    if (!_byKey.TryGetValue(processDefinitionKey, out var versions) || versions.Count == 0)
    {
        throw new KeyNotFoundException($"Workflow with id '{processDefinitionKey}' is not registered. Ensure the workflow is deployed before creating instances.");
    }

    var definition = versions[^1];

    if (!allowDisabled && !definition.IsActive)
    {
        throw new InvalidOperationException($"Process '{processDefinitionKey}' is disabled. Enable it before creating new instances.");
    }

    return definition;
}
```

Then update calls that should allow disabled processes:
- `DisableProcess` and `EnableProcess`: call `GetLatestDefinitionOrThrow(key, allowDisabled: true)`
- `DeployWorkflow` does NOT use this method (it accesses `_byKey` directly), so no change needed

**Step 6: Add auto-enable on deploy**

In `DeployWorkflow`, after `versions.Add(definition);` (line 143), add:

```csharp
// Auto-enable if previously disabled
foreach (var v in versions)
{
    if (!v.IsActive)
    {
        v.IsActive = true;
    }
}
```

Actually, simpler — the new definition `IsActive` defaults to `true` already, and it's the latest. But we should also re-enable the key-level state. Since `IsActive` is on each `ProcessDefinition` version and `GetLatestDefinitionOrThrow` checks the latest, the new deployment with `IsActive = true` naturally re-enables. No extra code needed — the new `ProcessDefinition` is constructed with `IsActive = true` by default.

**Step 7: Add log messages**

In `WorkflowInstanceFactoryGrain.cs`, add after existing `[LoggerMessage]` declarations:

```csharp
[LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Process {ProcessDefinitionKey} disabled")]
private partial void LogProcessDisabled(string processDefinitionKey);

[LoggerMessage(EventId = 6003, Level = LogLevel.Information, Message = "Process {ProcessDefinitionKey} enabled")]
private partial void LogProcessEnabled(string processDefinitionKey);
```

**Step 8: Run tests to verify they pass**

Run: `dotnet test src/Fleans/ --filter "DisableProcess|EnableProcess|DeployWorkflow_ShouldAutoEnable"`
Expected: PASS

**Step 9: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 10: Commit**

```bash
git add src/Fleans/Fleans.Application/WorkflowFactory/IWorkflowInstanceFactoryGrain.cs \
        src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs \
        src/Fleans/Fleans.Application.Tests/WorkflowInstanceFactoryGrainTests.cs
git commit -m "feat: implement DisableProcess/EnableProcess on factory grain"
```

---

### Task 6: Add `DisableProcess` / `EnableProcess` to `IWorkflowCommandService` and implementation

**Files:**
- Modify: `src/Fleans/Fleans.Application/IWorkflowCommandService.cs:7-15`
- Modify: `src/Fleans/Fleans.Application/WorkflowCommandService.cs`

**Step 1: Add methods to the interface**

In `IWorkflowCommandService.cs`, add after `SendSignal`:

```csharp
Task<ProcessDefinitionSummary> DisableProcess(string processDefinitionKey);
Task<ProcessDefinitionSummary> EnableProcess(string processDefinitionKey);
```

**Step 2: Implement in WorkflowCommandService**

In `WorkflowCommandService.cs`, add after the `DeployWorkflow` method (after line 61):

```csharp
public async Task<ProcessDefinitionSummary> DisableProcess(string processDefinitionKey)
{
    LogDisablingProcess(processDefinitionKey);
    var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
    return await factoryGrain.DisableProcess(processDefinitionKey);
}

public async Task<ProcessDefinitionSummary> EnableProcess(string processDefinitionKey)
{
    LogEnablingProcess(processDefinitionKey);
    var factoryGrain = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(WorkflowInstanceFactorySingletonId);
    return await factoryGrain.EnableProcess(processDefinitionKey);
}
```

Add log messages:

```csharp
[LoggerMessage(EventId = 7008, Level = LogLevel.Information, Message = "Disabling process {ProcessDefinitionKey}")]
private partial void LogDisablingProcess(string processDefinitionKey);

[LoggerMessage(EventId = 7009, Level = LogLevel.Information, Message = "Enabling process {ProcessDefinitionKey}")]
private partial void LogEnablingProcess(string processDefinitionKey);
```

**Step 3: Verify build**

Run: `dotnet build src/Fleans/`
Expected: BUILD SUCCEEDED

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application/IWorkflowCommandService.cs \
        src/Fleans/Fleans.Application/WorkflowCommandService.cs
git commit -m "feat: add DisableProcess/EnableProcess to command service"
```

---

### Task 7: Add API endpoints

**Files:**
- Modify: `src/Fleans/Fleans.Api/Controllers/WorkflowController.cs`
- Create: `src/Fleans/Fleans.ServiceDefaults/DTOs/ProcessDefinitionKeyRequest.cs`

**Step 1: Create DTO**

Create `src/Fleans/Fleans.ServiceDefaults/DTOs/ProcessDefinitionKeyRequest.cs`:

```csharp
namespace Fleans.ServiceDefaults.DTOs;

public record ProcessDefinitionKeyRequest(string ProcessDefinitionKey);
```

**Step 2: Add endpoints to WorkflowController**

In `WorkflowController.cs`, add after the `SendSignal` method (before the closing braces):

```csharp
[HttpPost("disable", Name = "DisableProcess")]
public async Task<IActionResult> DisableProcess([FromBody] ProcessDefinitionKeyRequest request)
{
    if (request == null || string.IsNullOrWhiteSpace(request.ProcessDefinitionKey))
        return BadRequest(new ErrorResponse("ProcessDefinitionKey is required"));

    try
    {
        var summary = await _commandService.DisableProcess(request.ProcessDefinitionKey);
        return Ok(summary);
    }
    catch (KeyNotFoundException ex)
    {
        return NotFound(new ErrorResponse(ex.Message));
    }
}

[HttpPost("enable", Name = "EnableProcess")]
public async Task<IActionResult> EnableProcess([FromBody] ProcessDefinitionKeyRequest request)
{
    if (request == null || string.IsNullOrWhiteSpace(request.ProcessDefinitionKey))
        return BadRequest(new ErrorResponse("ProcessDefinitionKey is required"));

    try
    {
        var summary = await _commandService.EnableProcess(request.ProcessDefinitionKey);
        return Ok(summary);
    }
    catch (KeyNotFoundException ex)
    {
        return NotFound(new ErrorResponse(ex.Message));
    }
}
```

**Step 3: Verify build**

Run: `dotnet build src/Fleans/`
Expected: BUILD SUCCEEDED

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Api/Controllers/WorkflowController.cs \
        src/Fleans/Fleans.ServiceDefaults/DTOs/ProcessDefinitionKeyRequest.cs
git commit -m "feat: add disable/enable API endpoints"
```

---

### Task 8: Add disable/enable button to Web UI

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/Workflows.razor`

**Step 1: Add disable/enable button to the Actions column**

In `Workflows.razor`, inside the Actions `<TemplateColumn>` (after the Instances button, around line 113), add:

```razor
@if (context.Item.IsActive)
{
    <FluentButton Appearance="Appearance.Stealth"
                  IconStart="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.Pause())"
                  Loading="@(isToggling && togglingProcessKey == context.Item.ProcessDefinitionKey)"
                  Disabled="@isToggling"
                  @onclick="() => ToggleActive(context.Item)">
        Disable
    </FluentButton>
}
else
{
    <FluentButton Appearance="Appearance.Stealth"
                  IconStart="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.Play())"
                  Loading="@(isToggling && togglingProcessKey == context.Item.ProcessDefinitionKey)"
                  Disabled="@isToggling"
                  @onclick="() => ToggleActive(context.Item)">
        Enable
    </FluentButton>
}
```

**Step 2: Add visual dimming for disabled processes**

Update the Process Key `<TemplateColumn>` (around line 86-88). Replace:

```razor
<TemplateColumn Title="Process Key" HierarchicalToggle="true">
    <strong title="@context.Item.ProcessDefinitionId">@context.Item.ProcessDefinitionKey</strong>
</TemplateColumn>
```

With:

```razor
<TemplateColumn Title="Process Key" HierarchicalToggle="true">
    <strong title="@context.Item.ProcessDefinitionId"
            style="@(context.Item.IsActive ? "" : "opacity: 0.5;")">
        @context.Item.ProcessDefinitionKey
        @if (!context.Item.IsActive)
        {
            <FluentBadge Color="Color.Warning" Style="margin-left: 8px;">Disabled</FluentBadge>
        }
    </strong>
</TemplateColumn>
```

**Step 3: Add state fields and handler in @code block**

In the `@code` block, add fields after `startingProcessDefinitionId` (around line 134):

```csharp
private bool isToggling = false;
private string? togglingProcessKey;
```

Add the handler method (after `ViewInstances` method, before the closing brace):

```csharp
private async Task ToggleActive(ProcessDefinitionSummary definition)
{
    try
    {
        isToggling = true;
        togglingProcessKey = definition.ProcessDefinitionKey;
        actionErrorMessage = null;
        actionSuccessMessage = null;

        if (definition.IsActive)
        {
            await CommandService.DisableProcess(definition.ProcessDefinitionKey);
            actionSuccessMessage = $"Disabled '{definition.ProcessDefinitionKey}'.";
        }
        else
        {
            await CommandService.EnableProcess(definition.ProcessDefinitionKey);
            actionSuccessMessage = $"Enabled '{definition.ProcessDefinitionKey}'.";
        }

        await LoadWorkflows();
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Error toggling process {ProcessDefinitionKey}", definition.ProcessDefinitionKey);
        actionErrorMessage = $"Failed to toggle process: {ex.Message}";
    }
    finally
    {
        isToggling = false;
        togglingProcessKey = null;
    }
}
```

**Step 4: Disable the Start button for disabled processes**

Update the Start button (around line 102-108). Add a condition to `Disabled`:

```razor
<FluentButton Appearance="Appearance.Stealth"
              IconStart="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.Play())"
              Loading="@(isStarting && startingProcessDefinitionId == context.Item.ProcessDefinitionId)"
              Disabled="@(isStarting || !context.Item.IsActive)"
              @onclick="() => StartVersion(context.Item)">
    Start
</FluentButton>
```

**Step 5: Verify build**

Run: `dotnet build src/Fleans/`
Expected: BUILD SUCCEEDED

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/Workflows.razor
git commit -m "feat: add disable/enable toggle to Web UI workflow list"
```

---

### Task 9: Refactor DeployWorkflow to use shared helpers

**Files:**
- Modify: `src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs`

Now that we have `RegisterAllStartEventListeners` and `UnregisterAllStartEventListeners` helpers, we can simplify `DeployWorkflow` to avoid code duplication. The existing inline registration/unregistration logic (lines 148-209) can be replaced.

**Step 1: Refactor DeployWorkflow**

Replace the entire start event registration block (lines 148-209) with:

```csharp
// Register start event listeners for the new version
await RegisterAllStartEventListeners(workflowWithId, processDefinitionKey, processDefinitionId);

// Unregister removed start event listeners from previous version
if (versions.Count > 1)
{
    var previousWorkflow = versions[^2].Workflow;

    // Timer: if previous had timer but new doesn't, deactivate
    if (!workflowWithId.Activities.OfType<TimerStartEvent>().Any()
        && previousWorkflow.Activities.OfType<TimerStartEvent>().Any())
    {
        var scheduler = _grainFactory.GetGrain<ITimerStartEventSchedulerGrain>(processDefinitionKey);
        await scheduler.DeactivateScheduler();
    }

    // Messages: unregister removed message names
    var newMessageNames = workflowWithId.Activities.OfType<MessageStartEvent>()
        .Select(ms => workflowWithId.Messages.FirstOrDefault(m => m.Id == ms.MessageDefinitionId)?.Name)
        .Where(n => n != null)
        .ToHashSet();

    foreach (var oldMessageStart in previousWorkflow.Activities.OfType<MessageStartEvent>())
    {
        var oldMsgDef = previousWorkflow.Messages.FirstOrDefault(m => m.Id == oldMessageStart.MessageDefinitionId);
        if (oldMsgDef != null && !newMessageNames.Contains(oldMsgDef.Name))
        {
            var listener = _grainFactory.GetGrain<IMessageStartEventListenerGrain>(oldMsgDef.Name);
            await listener.UnregisterProcess(processDefinitionKey);
        }
    }

    // Signals: unregister removed signal names
    var newSignalNames = workflowWithId.Activities.OfType<SignalStartEvent>()
        .Select(ss => workflowWithId.Signals.FirstOrDefault(s => s.Id == ss.SignalDefinitionId)?.Name)
        .Where(n => n != null)
        .ToHashSet();

    foreach (var oldSignalStart in previousWorkflow.Activities.OfType<SignalStartEvent>())
    {
        var oldSigDef = previousWorkflow.Signals.FirstOrDefault(s => s.Id == oldSignalStart.SignalDefinitionId);
        if (oldSigDef != null && !newSignalNames.Contains(oldSigDef.Name))
        {
            var listener = _grainFactory.GetGrain<ISignalStartEventListenerGrain>(oldSigDef.Name);
            await listener.UnregisterProcess(processDefinitionKey);
        }
    }
}
```

**Step 2: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass (refactor preserves behavior)

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application/WorkflowFactory/WorkflowInstanceFactoryGrain.cs
git commit -m "refactor: use shared helpers for start event registration in DeployWorkflow"
```

---

### Task 10: Add test for signal start event unregistration on disable

**Files:**
- Modify: `src/Fleans/Fleans.Application.Tests/WorkflowInstanceFactoryGrainTests.cs`

**Step 1: Add the test**

```csharp
[TestMethod]
public async Task DisableProcess_ShouldUnregisterSignalStartEventListener()
{
    // Arrange — deploy with a SignalStartEvent
    var processKey = "signal-disable-test";
    var signalName = "test-signal";
    var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

    var signalStart = new SignalStartEvent("signalStart")
    {
        SignalDefinitionId = "sig1"
    };
    var end = new EndEvent("end");
    var workflow = new WorkflowDefinition
    {
        WorkflowId = processKey,
        Activities = new List<Activity> { signalStart, end },
        SequenceFlows = new List<SequenceFlow>
        {
            new SequenceFlow("seq1", signalStart, end)
        },
        Signals = new List<SignalDefinition>
        {
            new SignalDefinition("sig1", signalName)
        }
    };
    await factoryGrain.DeployWorkflow(workflow, "<bpmn/>");

    // Act
    await factoryGrain.DisableProcess(processKey);

    // Assert — the signal listener should have no registered processes
    var listener = _cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>(signalName);
    var instanceIds = await listener.FireSignalStartEvent();
    Assert.AreEqual(0, instanceIds.Count);
}

[TestMethod]
public async Task EnableProcess_ShouldReregisterSignalStartEventListener()
{
    // Arrange — deploy with a SignalStartEvent, then disable
    var processKey = "signal-enable-test";
    var signalName = "test-signal-enable";
    var factoryGrain = _cluster.GrainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);

    var signalStart = new SignalStartEvent("signalStart")
    {
        SignalDefinitionId = "sig1"
    };
    var end = new EndEvent("end");
    var workflow = new WorkflowDefinition
    {
        WorkflowId = processKey,
        Activities = new List<Activity> { signalStart, end },
        SequenceFlows = new List<SequenceFlow>
        {
            new SequenceFlow("seq1", signalStart, end)
        },
        Signals = new List<SignalDefinition>
        {
            new SignalDefinition("sig1", signalName)
        }
    };
    await factoryGrain.DeployWorkflow(workflow, "<bpmn/>");
    await factoryGrain.DisableProcess(processKey);

    // Act
    await factoryGrain.EnableProcess(processKey);

    // Assert — the signal listener should fire and create an instance
    var listener = _cluster.GrainFactory.GetGrain<ISignalStartEventListenerGrain>(signalName);
    var instanceIds = await listener.FireSignalStartEvent();
    Assert.AreEqual(1, instanceIds.Count);
}
```

**Step 2: Ensure the SiloConfigurator registers all required grain storage**

In the test's `SiloConfigurator`, verify these storage names are registered (add any missing):

```csharp
public void Configure(ISiloBuilder hostBuilder) =>
    hostBuilder
        .AddMemoryGrainStorage(GrainStorageNames.WorkflowInstances)
        .AddMemoryGrainStorage(GrainStorageNames.ActivityInstances)
        .AddMemoryGrainStorage(GrainStorageNames.ProcessDefinitions)
        .AddMemoryGrainStorage(GrainStorageNames.TimerSchedulers)
        .AddMemoryGrainStorage(GrainStorageNames.MessageStartEventListeners)
        .AddMemoryGrainStorage(GrainStorageNames.SignalStartEventListeners)
        .AddMemoryGrainStorage(GrainStorageNames.MessageCorrelations)
        .AddMemoryGrainStorage(GrainStorageNames.SignalCorrelations)
        .UseInMemoryReminderService()
        .ConfigureServices(services =>
        {
            services.AddSingleton<IProcessDefinitionRepository, StubProcessDefinitionRepository>();
            services.AddTransient<IBoundaryEventHandler, BoundaryEventHandler>();
        });
```

Note: `UseInMemoryReminderService()` is needed for `TimerStartEventSchedulerGrain` which uses Orleans reminders.

**Step 3: Run tests**

Run: `dotnet test src/Fleans/ --filter "DisableProcess_ShouldUnregister|EnableProcess_ShouldReregister"`
Expected: PASS

**Step 4: Run all tests**

Run: `dotnet test src/Fleans/`
Expected: All tests pass

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/WorkflowInstanceFactoryGrainTests.cs
git commit -m "test: add signal start event unregistration tests for disable/enable"
```

---

### Task 11: Add manual test plan

**Files:**
- Create: `tests/manual/18-start-event-undeploy/test-plan.md`
- Create: `tests/manual/18-start-event-undeploy/signal-start-disable.bpmn`

**Step 1: Create BPMN fixture**

Create `tests/manual/18-start-event-undeploy/signal-start-disable.bpmn` — a simple workflow with a `SignalStartEvent` listening for signal `"test-disable-signal"`, followed by a `ScriptTask` and `EndEvent`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
             xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
             xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
             xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
             id="Definitions_1"
             targetNamespace="http://bpmn.io/schema/bpmn">

  <signal id="Signal_1" name="test-disable-signal" />

  <process id="signal-start-disable-test" isExecutable="true">
    <startEvent id="SignalStart" name="Signal Start">
      <signalEventDefinition signalRef="Signal_1" />
    </startEvent>

    <scriptTask id="Script1" name="Log" scriptFormat="csharp">
      <script>Console.WriteLine("Signal received, workflow started!");</script>
    </scriptTask>

    <endEvent id="End1" name="End" />

    <sequenceFlow id="Flow1" sourceRef="SignalStart" targetRef="Script1" />
    <sequenceFlow id="Flow2" sourceRef="Script1" targetRef="End1" />
  </process>

  <bpmndi:BPMNDiagram id="BPMNDiagram_1">
    <bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="signal-start-disable-test">
      <bpmndi:BPMNShape id="SignalStart_di" bpmnElement="SignalStart">
        <dc:Bounds x="180" y="100" width="36" height="36" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="Script1_di" bpmnElement="Script1">
        <dc:Bounds x="270" y="78" width="100" height="80" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="End1_di" bpmnElement="End1">
        <dc:Bounds x="430" y="100" width="36" height="36" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNEdge id="Flow1_di" bpmnElement="Flow1">
        <di:waypoint x="216" y="118" />
        <di:waypoint x="270" y="118" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id="Flow2_di" bpmnElement="Flow2">
        <di:waypoint x="370" y="118" />
        <di:waypoint x="430" y="118" />
      </bpmndi:BPMNEdge>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</definitions>
```

**Step 2: Create test plan**

Create `tests/manual/18-start-event-undeploy/test-plan.md`:

```markdown
# 18 — Start Event Undeploy (Disable/Enable)

## Scenario
Verify that disabling a process definition stops its start event listeners and enabling re-registers them.

## Prerequisites
- Aspire stack running: `dotnet run --project Fleans.Aspire`
- Web UI at https://localhost:7175
- API at https://localhost:7140

## Steps

### 1. Deploy the fixture
Upload `signal-start-disable.bpmn` via Web UI Editor or API.

### 2. Verify signal starts a new instance
```
POST https://localhost:7140/Workflow/signal
{"SignalName": "test-disable-signal"}
```
- [ ] Response includes a `WorkflowInstanceIds` list with one ID
- [ ] Instance visible in Web UI under `signal-start-disable-test`

### 3. Disable the process
```
POST https://localhost:7140/Workflow/disable
{"ProcessDefinitionKey": "signal-start-disable-test"}
```
- [ ] Response `IsActive` is `false`
- [ ] Web UI shows "Disabled" badge and dimmed row

### 4. Verify signal no longer starts instances
```
POST https://localhost:7140/Workflow/signal
{"SignalName": "test-disable-signal"}
```
- [ ] Response: 404 (no subscription or start event found)
- [ ] No new instances created

### 5. Verify manual start is blocked
```
POST https://localhost:7140/Workflow/start
{"WorkflowId": "signal-start-disable-test"}
```
- [ ] Response: error (process is disabled)
- [ ] Web UI Start button is disabled

### 6. Re-enable the process
```
POST https://localhost:7140/Workflow/enable
{"ProcessDefinitionKey": "signal-start-disable-test"}
```
- [ ] Response `IsActive` is `true`
- [ ] Web UI badge removed, row no longer dimmed

### 7. Verify signal starts instances again
```
POST https://localhost:7140/Workflow/signal
{"SignalName": "test-disable-signal"}
```
- [ ] Response includes `WorkflowInstanceIds` with one ID
- [ ] Instance visible in Web UI
```

**Step 3: Commit**

```bash
git add tests/manual/18-start-event-undeploy/
git commit -m "test: manual test plan for start event disable/enable"
```

---

### Task Summary

| Task | Description | Type |
|------|-------------|------|
| 1 | Add `IsActive` to `ProcessDefinition` + EF config | Domain |
| 2 | Add `IsActive` to `ProcessDefinitionSummary` + query | Application |
| 3 | Fix timer deactivation on redeployment | Bug fix |
| 4 | Add interface methods | Interface |
| 5 | Implement `DisableProcess`/`EnableProcess` + guards | Core logic |
| 6 | Wire through `IWorkflowCommandService` | Service layer |
| 7 | Add API endpoints | API |
| 8 | Add Web UI toggle | UI |
| 9 | Refactor DeployWorkflow to use shared helpers | Refactor |
| 10 | Signal start event disable/enable tests | Tests |
| 11 | Manual test plan + BPMN fixture | Manual testing |
