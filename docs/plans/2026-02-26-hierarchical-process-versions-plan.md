# Hierarchical Process Versions Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the flat process-definitions DataGrid with a hierarchical one that groups versions under their process key, using Fluent UI Blazor 4.14's `IHierarchicalGridItem`.

**Architecture:** UI-only change. Create a `ProcessDefinitionRow` wrapper that implements `IHierarchicalGridItem`, group `ProcessDefinitionSummary` records by key (latest version = parent, older = children), and swap the `FluentDataGrid<ProcessDefinitionSummary>` for `FluentDataGrid<ProcessDefinitionRow>` with `HierarchicalToggle` on the first column.

**Tech Stack:** Fluent UI Blazor 4.14.0, Blazor Server (InteractiveServer), C# 14

**Design doc:** `docs/plans/2026-02-26-hierarchical-process-versions-design.md`

---

### Task 1: Create feature branch

**Step 1: Create and switch to feature branch**

Run:
```bash
cd /Users/yerassylshalabayev/RiderProjects/fleans
git checkout -b feature/hierarchical-process-versions main
```

---

### Task 2: Bump Fluent UI Blazor packages to 4.14.0

**Files:**
- Modify: `src/Fleans/Fleans.Web/Fleans.Web.csproj`

**Step 1: Update package versions**

In `Fleans.Web.csproj`, change these three `PackageReference` versions from `4.13.2` to `4.14.0`:

```xml
<PackageReference Include="Microsoft.FluentUI.AspNetCore.Components" Version="4.14.0" />
<PackageReference Include="Microsoft.FluentUI.AspNetCore.Components.Emoji" Version="4.14.0" />
<PackageReference Include="Microsoft.FluentUI.AspNetCore.Components.Icons" Version="4.14.0" />
```

**Step 2: Restore and build**

Run:
```bash
cd /Users/yerassylshalabayev/RiderProjects/fleans/src/Fleans
dotnet restore Fleans.Web/Fleans.Web.csproj
dotnet build
```

Expected: Build succeeds with no errors. There may be new warnings from the package update — note them but don't fix unless they're errors.

**Step 3: Run tests to verify no regressions**

Run:
```bash
cd /Users/yerassylshalabayev/RiderProjects/fleans/src/Fleans
dotnet test
```

Expected: All existing tests pass. The package update is Fluent UI only (Web project) — domain/application tests should be unaffected.

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Web/Fleans.Web.csproj
git commit -m "chore: bump Fluent UI Blazor packages to 4.14.0

Upgrades from 4.13.2 to 4.14.0 to gain access to
IHierarchicalGridItem and hierarchical DataGrid support."
```

---

### Task 3: Add `ProcessDefinitionRow` and convert `Workflows.razor` to hierarchical grid

**Files:**
- Modify: `src/Fleans/Fleans.Web/Components/Pages/Workflows.razor`

**Step 1: Add the `ProcessDefinitionRow` class inside `@code`**

Add this class at the top of the `@code` block in `Workflows.razor`. It wraps `ProcessDefinitionSummary` and implements `IHierarchicalGridItem`:

```csharp
private sealed class ProcessDefinitionRow : IHierarchicalGridItem
{
    public required ProcessDefinitionSummary Summary { get; init; }
    public required int Depth { get; set; }
    public required bool HasChildren { get; set; }
    public bool IsCollapsed { get; set; } = true;
    public bool IsHidden { get; set; }
    public List<ProcessDefinitionRow> Children { get; init; } = [];
}
```

**Step 2: Replace state fields**

Replace these three fields:

```csharp
private List<ProcessDefinitionSummary> allDefinitions = new();
private List<ProcessDefinitionSummary> filteredDefinitions = new();
private IQueryable<ProcessDefinitionSummary> filteredDefinitionsQueryable = Enumerable.Empty<ProcessDefinitionSummary>().AsQueryable();
```

With:

```csharp
private List<ProcessDefinitionRow> allRows = [];
private List<ProcessDefinitionRow> filteredRows = [];
private IQueryable<ProcessDefinitionRow> filteredRowsQueryable = Enumerable.Empty<ProcessDefinitionRow>().AsQueryable();
```

**Step 3: Replace `LoadWorkflows()` data transformation**

Replace the body of the `try` block in `LoadWorkflows()` (lines 158–168) with:

```csharp
isLoading = true;
loadErrorMessage = null;

var definitions = await QueryService.GetAllProcessDefinitions();

allRows = definitions
    .GroupBy(d => d.ProcessDefinitionKey)
    .OrderBy(g => g.Key, StringComparer.Ordinal)
    .SelectMany(group =>
    {
        var versions = group.OrderByDescending(d => d.Version).ToList();
        var children = versions.Skip(1)
            .Select(d => new ProcessDefinitionRow
            {
                Summary = d,
                Depth = 1,
                HasChildren = false,
                IsHidden = true,
            })
            .ToList();

        var parent = new ProcessDefinitionRow
        {
            Summary = versions[0],
            Depth = 0,
            HasChildren = children.Count > 0,
            Children = children,
        };

        return Enumerable.Repeat(parent, 1).Concat(children);
    })
    .ToList();

ApplyFilter();
```

**Step 4: Replace `ApplyFilter()`**

Replace the entire `ApplyFilter()` method with:

```csharp
private void ApplyFilter()
{
    if (string.IsNullOrWhiteSpace(SearchQuery))
    {
        filteredRows = allRows;
    }
    else
    {
        // Include parent + its children when key matches
        var matchingKeys = allRows
            .Where(r => r.Depth == 0
                && r.Summary.ProcessDefinitionKey.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Summary.ProcessDefinitionKey)
            .ToHashSet(StringComparer.Ordinal);

        filteredRows = allRows
            .Where(r => matchingKeys.Contains(r.Summary.ProcessDefinitionKey))
            .ToList();
    }

    filteredRowsQueryable = filteredRows.AsQueryable();
}
```

**Step 5: Update the empty-state check in the template**

In the template, replace `allDefinitions.Count` references (lines 30 and 65) with `allRows.Count`:

- Line 30: `@if (isLoading && allRows.Count == 0)`
- Line 65: `@if (allRows.Count == 0)`

Replace `filteredDefinitions.Count` (line 77) with `filteredRows.Count`:

- Line 77: `@if (filteredRows.Count == 0)`

**Step 6: Update the error handler's empty-list guard**

In the `catch` block of `LoadWorkflows()`, replace:

```csharp
if (allDefinitions.Count == 0)
{
    filteredDefinitions.Clear();
    filteredDefinitionsQueryable = filteredDefinitions.AsQueryable();
}
```

With:

```csharp
if (allRows.Count == 0)
{
    filteredRows = [];
    filteredRowsQueryable = filteredRows.AsQueryable();
}
```

**Step 7: Replace the `FluentDataGrid` in the template**

Replace the entire `<FluentDataGrid>` block (lines 85–116) with:

```razor
<FluentDataGrid Items="@filteredRowsQueryable" TGridItem="ProcessDefinitionRow">
    <TemplateColumn Title="Process Key" HierarchicalToggle="true">
        <strong title="@context.Summary.ProcessDefinitionId">@context.Summary.ProcessDefinitionKey</strong>
    </TemplateColumn>
    <TemplateColumn Title="Version">
        <FluentBadge Color="Color.Accent">v@(context.Summary.Version)</FluentBadge>
    </TemplateColumn>
    <TemplateColumn Title="Deployed at (UTC)">
        @context.Summary.DeployedAt.ToUniversalTime().ToString("u")
    </TemplateColumn>
    <TemplateColumn Title="Actions">
        <FluentStack Orientation="Orientation.Horizontal" Gap="4px">
            <FluentButton Appearance="Appearance.Stealth"
                          IconStart="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.Edit())"
                          @onclick="() => EditWorkflow(context.Summary)">
                Edit
            </FluentButton>
            <FluentButton Appearance="Appearance.Stealth"
                          IconStart="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.Play())"
                          Loading="@(isStarting && startingProcessDefinitionId == context.Summary.ProcessDefinitionId)"
                          Disabled="@isStarting"
                          @onclick="() => StartVersion(context.Summary)">
                Start
            </FluentButton>
            <FluentButton Appearance="Appearance.Stealth"
                          IconStart="@(new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.List())"
                          @onclick="() => ViewInstances(context.Summary)">
                Instances
            </FluentButton>
        </FluentStack>
    </TemplateColumn>
</FluentDataGrid>
```

Key changes from the old grid:
- `TGridItem` is now `ProcessDefinitionRow` (not `ProcessDefinitionSummary`)
- `Items` is now `filteredRowsQueryable`
- First column has `HierarchicalToggle="true"`
- All `context.Xxx` references become `context.Summary.Xxx`

**Step 8: Update action methods to accept `ProcessDefinitionSummary`**

The `EditWorkflow`, `StartVersion`, and `ViewInstances` methods already accept `ProcessDefinitionSummary` — the template now passes `context.Summary` so no signature changes needed. Verify the method signatures are unchanged:

```csharp
private void EditWorkflow(ProcessDefinitionSummary definition) { ... }
private async Task StartVersion(ProcessDefinitionSummary version) { ... }
private void ViewInstances(ProcessDefinitionSummary version) { ... }
```

**Step 9: Build and verify**

Run:
```bash
cd /Users/yerassylshalabayev/RiderProjects/fleans/src/Fleans
dotnet build
```

Expected: Build succeeds with no errors.

**Step 10: Run tests**

Run:
```bash
cd /Users/yerassylshalabayev/RiderProjects/fleans/src/Fleans
dotnet test
```

Expected: All tests pass.

**Step 11: Commit**

```bash
git add src/Fleans/Fleans.Web/Components/Pages/Workflows.razor
git commit -m "feat: hierarchical process versions in DataGrid

Group process definitions by key. Latest version is the parent row,
older versions are expandable children. Uses IHierarchicalGridItem
from Fluent UI Blazor 4.14."
```

---

### Task 4: Manual smoke test

**Step 1: Start the application**

Run:
```bash
cd /Users/yerassylshalabayev/RiderProjects/fleans/src/Fleans
dotnet run --project Fleans.Aspire
```

**Step 2: Verify in browser**

1. Navigate to `https://localhost:...` (Web UI port from Aspire dashboard)
2. Go to the Workflows page
3. If there are no processes, deploy a BPMN file via the Editor page, then deploy a second version of the same process
4. Verify:
   - Processes with a single version show as a flat row (no expand chevron)
   - Processes with multiple versions show an expand chevron on the parent row
   - Clicking the chevron reveals older version rows indented beneath
   - Search filter works — filtering by key shows matching parent + children
   - Edit / Start / Instances buttons work on both parent and child rows
5. Stop the application with Ctrl+C

---

### Task 5: Final commit and PR readiness

**Step 1: Verify clean state**

Run:
```bash
cd /Users/yerassylshalabayev/RiderProjects/fleans
git status
git log --oneline main..HEAD
```

Expected: Two commits on `feature/hierarchical-process-versions`:
1. `chore: bump Fluent UI Blazor packages to 4.14.0`
2. `feat: hierarchical process versions in DataGrid`

No uncommitted changes.
